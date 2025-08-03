import onnxruntime

model = onnxruntime.InferenceSession("vocals.onnx", providers=['CPUExecutionProvider'])
input_shape = model.get_inputs()[0].shape

print(input_shape)