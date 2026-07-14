from ultralytics import YOLO

model = YOLO("yolo11s-seg.pt")
model.export(format="onnx")