"""Numerical oracle for the tiled depth-reduction design (stdlib only).

This compares a tile/owner-lane emulation with a direct per-object reference.
It does not execute HLSL; Validate-Shaders.ps1 compiles the actual shader.
The reference is also suitable for comparing real GPU output in Unity.
"""
import math
import random
import unittest


def valid(value, maximum):
    return math.isfinite(value) and 0 < value <= maximum


def interior(mask, width, height, x, y, obj):
    return (0 < x < width - 1 and 0 < y < height - 1
            and all(mask[yy * width + xx] == obj
                    for xx, yy in ((x - 1, y), (x + 1, y), (x, y - 1), (x, y + 1))))


def reference(mask, depth, width, height, count, capacity, scale=1, offset=0, maximum=1e6):
    result = []
    for obj in range(capacity):
        full, inside = [], []
        if obj < count:
            for y in range(height):
                for x in range(width):
                    index = y * width + x
                    if mask[index] == obj and valid(depth[index], maximum):
                        full.append(depth[index])
                        if interior(mask, width, height, x, y, obj):
                            inside.append(depth[index])
        values = inside or full
        value = sum(values) / len(values) * scale + offset if values else math.nan
        result.append(value if math.isfinite(value) and value > 0 else math.nan)
    return result


def tiled(mask, depth, width, height, count, capacity, scale=1, offset=0, maximum=1e6):
    partials = []
    for gy in range((height + 7) // 8):
        for gx in range((width + 7) // 8):
            sums = [[0., 0., 0., 0.] for _ in range(count)]
            ids, values, eroded = [], [], []
            for lane in range(64):
                x, y = gx * 8 + lane % 8, gy * 8 + lane // 8
                good = (x < width and y < height
                        and 0 <= mask[y * width + x] < count
                        and valid(depth[y * width + x], maximum))
                obj = mask[y * width + x] if good else -1
                ids.append(obj)
                values.append(depth[y * width + x] if good else 0)
                eroded.append(good and interior(mask, width, height, x, y, obj))
            for lane, obj in enumerate(ids):
                if obj < 0 or obj in ids[:lane]:
                    continue
                for other in range(64):
                    if ids[other] == obj:
                        sums[obj][2] += values[other]
                        sums[obj][3] += 1
                        if eroded[other]:
                            sums[obj][0] += values[other]
                            sums[obj][1] += 1
            partials.append(sums)
    result = []
    for obj in range(capacity):
        if obj >= count:
            result.append(math.nan)
            continue
        sums = [sum(tile[obj][axis] for tile in partials) for axis in range(4)]
        total, pixels = sums[:2] if sums[1] > 0 else sums[2:]
        value = total / pixels * scale + offset if pixels else math.nan
        result.append(value if math.isfinite(value) and value > 0 else math.nan)
    return result


class DepthReductionTests(unittest.TestCase):
    def test_randomized_tiles_match_independent_reference(self):
        rng = random.Random(1729)
        for width, height in [(1, 1), (2, 9), (7, 7), (8, 8), (9, 17), (31, 19)]:
            for count in [0, 1, 4, 9]:
                for trial in range(10):
                    mask = [rng.randrange(-1, max(1, count + 2)) for _ in range(width * height)]
                    depth = [rng.choice([math.nan, math.inf, -1, 0, 1e7, rng.random() * 50]) for _ in mask]
                    expected = reference(mask, depth, width, height, count, 12, 2.5, -.2)
                    actual = tiled(mask, depth, width, height, count, 12, 2.5, -.2)
                    with self.subTest(width=width, height=height, count=count, trial=trial):
                        for a, b in zip(expected, actual):
                            self.assertTrue((math.isnan(a) and math.isnan(b)) or math.isclose(a, b, rel_tol=1e-12))

    def test_interior_rejects_contaminated_edges(self):
        mask = [0] * 49
        depth = [2 if interior(mask, 7, 7, x, y, 0) else 999 for y in range(7) for x in range(7)]
        self.assertEqual(tiled(mask, depth, 7, 7, 1, 3)[0], 2)

    def test_tiny_object_fallback_and_unavailable_values(self):
        self.assertEqual(tiled([0], [7], 1, 1, 1, 3)[0], 7)
        for mask, depth, count in [([-1], [7], 1), ([0], [0], 1), ([0], [math.nan], 1), ([0], [7], 0)]:
            self.assertTrue(all(math.isnan(v) for v in tiled(mask, depth, 1, 1, count, 3)))

    def test_unused_slots_cannot_retain_previous_object_depth(self):
        first = tiled([0, 1], [2, 8], 2, 1, 2, 4)
        second = tiled([0, -1], [3, 8], 2, 1, 1, 4)
        self.assertEqual(first[:2], [2, 8])
        self.assertEqual(second[0], 3)
        self.assertTrue(all(math.isnan(v) for v in second[1:]))


if __name__ == '__main__':
    unittest.main(verbosity=2)
