"""Contract tests that require Pillow/numpy, not model weights or a GPU."""
import unittest
import base64
import io
import json
import threading
import urllib.error
import urllib.request
import numpy as np
from PIL import Image, ImageDraw
from moebius import Settings, prepare_images, composite, make_server, png_bytes


class ImageContractTests(unittest.TestCase):
    def test_portrait_edge_removal_preserves_unmasked_pixels(self):
        rng = np.random.default_rng(42)
        original = rng.integers(0, 256, (151, 73, 3), dtype=np.uint8)
        image = Image.fromarray(original)
        mask = Image.new('L', image.size)
        ImageDraw.Draw(mask).rectangle((0, 12, 17, 70), fill=255)
        settings = Settings(dilation=0, feather=3)
        image, binary, box, crop, crop_mask = prepare_images(image, mask, settings)
        self.assertEqual(crop.size, (512, 512))
        self.assertLess(box[0], 0)  # context can extend beyond image bounds
        result, overlay = composite(image, binary, box, Image.new('RGB', crop.size, 'red'), settings.feather)
        actual = np.asarray(result)
        removed = np.asarray(binary) > 0
        self.assertTrue(np.array_equal(actual[~removed], original[~removed]))
        self.assertTrue((np.asarray(overlay)[~removed, 3] == 0).all())
        self.assertFalse(np.array_equal(actual[removed], original[removed]))
        self.assertEqual(result.size, (73, 151))

    def test_black_mask_is_noop_and_white_mask_is_removal(self):
        image = Image.new('RGB', (160, 90), (70, 120, 10))
        black = Image.new('L', image.size)
        _, binary, box, _, _ = prepare_images(image, black, Settings())
        result, overlay = composite(image, binary, box, None, 3)
        self.assertIsNone(box)
        self.assertEqual(result.tobytes(), image.tobytes())
        self.assertIsNone(overlay.getbbox())
        white = Image.new('L', image.size, 255)
        _, _, box, crop, _ = prepare_images(image, white, Settings())
        self.assertIsNotNone(box)
        self.assertEqual(crop.size, (512, 512))

    def test_dilation_expands_mask_and_mismatched_dimensions_fail(self):
        image = Image.new('RGB', (128, 128))
        mask = Image.new('L', image.size)
        mask.putpixel((60, 60), 255)
        _, binary, _, _, _ = prepare_images(image, mask, Settings(dilation=3))
        self.assertEqual(binary.getbbox(), (57, 57, 64, 64))
        with self.assertRaises(ValueError):
            prepare_images(image, Image.new('L', (64, 64)), Settings())

    def test_square_context_does_not_stretch_asymmetric_mask(self):
        image = Image.new('RGB', (300, 100))
        mask = Image.new('L', image.size)
        ImageDraw.Draw(mask).rectangle((100, 40, 199, 59), fill=255)
        _, _, box, _, crop_mask = prepare_images(image, mask, Settings(dilation=0))
        bounds = crop_mask.getbbox()
        ratio = (bounds[2] - bounds[0]) / (bounds[3] - bounds[1])
        self.assertAlmostEqual(ratio, 5, delta=0.15)
        self.assertEqual(box[2] - box[0], box[3] - box[1])

    def test_border_context_repeats_observed_rgb_and_does_not_create_removal_pixels(self):
        image = Image.new('RGB', (73, 151), (177, 133, 99))
        mask = Image.new('L', image.size)
        ImageDraw.Draw(mask).rectangle((0, 12, 17, 70), fill=255)
        _, _, box, crop, crop_mask = prepare_images(image, mask, Settings(dilation=0))
        self.assertLess(box[0], 0)
        self.assertEqual(crop.getpixel((0, 0)), (177, 133, 99))
        self.assertEqual(crop_mask.getpixel((0, 0)), 0)
        with self.assertRaises(ValueError):
            Settings(resolution=256).validate()


class BridgeContractTests(unittest.TestCase):
    def setUp(self):
        class ContractEngine:
            load_ms = 0
            def infer(self, image, mask, settings):
                image, binary, box, crop, _ = prepare_images(image, mask, settings)
                result, overlay = composite(image, binary, box, Image.new('RGB', crop.size, 'red') if crop else None, 0)
                return result, overlay, {'native_pipeline_ms': 0}
        self.server = make_server(ContractEngine(), 0, Settings(dilation=0, feather=0))
        self.thread = threading.Thread(target=self.server.serve_forever, daemon=True)
        self.thread.start()
        self.base = f'http://127.0.0.1:{self.server.server_port}'

    def tearDown(self):
        self.server.shutdown()
        self.server.server_close()
        self.thread.join(2)

    def test_roundtrip_preserves_frame_id_dimensions_alpha_and_orientation(self):
        image = Image.new('RGB', (80, 40), 'blue')
        mask = Image.new('L', image.size)
        ImageDraw.Draw(mask).rectangle((5, 3, 20, 10), fill=255)  # asymmetric top-left target
        payload = json.dumps({'frame_id': 729, 'image': base64.b64encode(png_bytes(image)).decode(),
                              'mask': base64.b64encode(png_bytes(mask)).decode()}).encode()
        request = urllib.request.Request(self.base + '/inpaint', data=payload, headers={'Content-Type': 'application/json'})
        with urllib.request.urlopen(request) as response:
            result = json.load(response)
        self.assertEqual(result['frame_id'], 729)
        overlay = Image.open(io.BytesIO(base64.b64decode(result['overlay'])))
        self.assertEqual(overlay.size, image.size)
        self.assertEqual(overlay.getpixel((10, 5)), (255, 0, 0, 255))
        self.assertEqual(overlay.getpixel((10, 34))[3], 0)

    def test_browser_origins_and_invalid_pngs_are_rejected(self):
        for headers, payload, expected in [
            ({'Content-Type': 'application/json', 'Origin': 'https://example.com'}, b'{}', 403),
            ({'Content-Type': 'text/plain'}, b'{}', 415),
            ({'Content-Type': 'application/json'}, b'{"image":"??","mask":"??"}', 400),
        ]:
            request = urllib.request.Request(self.base + '/inpaint', data=payload, headers=headers)
            with self.assertRaises(urllib.error.HTTPError) as caught:
                urllib.request.urlopen(request)
            self.assertEqual(caught.exception.code, expected)


if __name__ == '__main__':
    unittest.main()
