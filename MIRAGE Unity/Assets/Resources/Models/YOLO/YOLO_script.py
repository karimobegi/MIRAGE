from ultralytics import YOLO

model = YOLO("yolo26s-seg.pt")
model.export(format="onnx", end2end=False)