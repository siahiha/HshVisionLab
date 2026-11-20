"""Export the official PaddleClas vehicle_attribute inference model to ONNX.

Usage: python convert_pplcnet_vehicle_color.py RAW_MODEL_DIRECTORY
Build-only dependencies (isolated Python 3.10 environment):
  paddlepaddle==2.6.2 paddle2onnx==1.3.1 onnx==1.16.2 numpy==1.26.4 onnxruntime
Download/extract the official vehicle_attribute_infer.tar into RAW/pplcnet first.
No training, pickle checkpoints or project runtime dependency on Paddle.
"""
import hashlib
import json
import sys
from pathlib import Path
import numpy as np
import onnx
import onnxruntime as ort
import paddle
import paddle2onnx

root = Path(sys.argv[1])
source = root / 'pplcnet' / 'vehicle_attribute_infer'
model_file, params_file = source / 'inference.pdmodel', source / 'inference.pdiparams'
expected_hashes = {
    root / 'vehicle_attribute_infer.tar': 'bba326342be959292ec628473d82a0dd9cea43d115e3b4364b4244a0fd104c8b',
    model_file: '71f841019fdf0eec60bac8aee7b819173b28ada2b02e2e718a76de55c3793643',
    params_file: '795677bdbf999cd9e72d648eb4b25917ccfa718b624d23a5653aa10001fd775c',
}
for path, expected_hash in expected_hashes.items():
    assert hashlib.sha256(path.read_bytes()).hexdigest() == expected_hash, 'Unexpected Paddle model source: ' + path.name
stem = 'PP-LCNet_x1_0_vehicle_attribute'
target = root / (stem + '.onnx')
paddle2onnx.export(model_filename=str(model_file), params_filename=str(params_file),
    save_file=str(target), opset_version=14, enable_onnx_checker=True,
    auto_upgrade_opset=False, verbose=False)
model = onnx.load(target)
assert len(model.graph.input) == len(model.graph.output) == 1
assert [d.dim_value for d in model.graph.input[0].type.tensor_type.shape.dim][1:] == [3, 192, 256]
assert model.graph.output[0].type.tensor_type.shape.dim[1].dim_value == 19
# The application processes one vehicle at a time. Freeze only the dynamic batch.
model.graph.input[0].type.tensor_type.shape.dim[0].dim_value = 1
model.graph.output[0].type.tensor_type.shape.dim[0].dim_value = 1
onnx.checker.check_model(model)
onnx.save(model, target)

config = paddle.inference.Config(str(model_file), str(params_file))
config.disable_gpu()
config.set_cpu_math_library_num_threads(1)
config.disable_glog_info()
config.switch_ir_optim(False)
predictor = paddle.inference.create_predictor(config)
source_input = predictor.get_input_handle(predictor.get_input_names()[0])
source_output = predictor.get_output_handle(predictor.get_output_names()[0])
options = ort.SessionOptions()
options.intra_op_num_threads = options.inter_op_num_threads = 1
session = ort.InferenceSession(str(target), options, providers=['CPUExecutionProvider'])
errors = []
for seed in range(5):
    rgb = np.random.default_rng(seed).uniform(0, 255, (1, 3, 192, 256)).astype(np.float32)
    array = (rgb / 255 - np.array([.485, .456, .406], np.float32)[None, :, None, None]) / np.array([.229, .224, .225], np.float32)[None, :, None, None]
    source_input.reshape(array.shape)
    source_input.copy_from_cpu(array)
    predictor.run()
    expected = source_output.copy_to_cpu()
    actual = session.run(None, {session.get_inputs()[0].name: array})[0]
    errors.append(float(np.max(np.abs(expected - actual))))
    assert np.allclose(expected, actual, atol=2e-5, rtol=2e-4), errors[-1]
    assert np.isfinite(actual).all() and ((actual >= 0) & (actual <= 1)).all()
report = {
    'source_url': 'https://paddleclas.bj.bcebos.com/models/PULC/vehicle_attribute_infer.tar',
    'source_sha256': {p.name: hashlib.sha256(p.read_bytes()).hexdigest() for p in [root / 'vehicle_attribute_infer.tar', model_file, params_file]},
    'onnx_sha256': hashlib.sha256(target.read_bytes()).hexdigest(),
    'versions': {'paddlepaddle': paddle.__version__, 'paddle2onnx': paddle2onnx.__version__, 'onnx': onnx.__version__},
    'input': {'name': session.get_inputs()[0].name, 'shape': session.get_inputs()[0].shape, 'color_order': 'RGB', 'scale': 1/255, 'mean': [.485,.456,.406], 'std': [.229,.224,.225]},
    'output': {'name': session.get_outputs()[0].name, 'shape': session.get_outputs()[0].shape, 'kind': 'independent sigmoid probabilities', 'color_indices': [0, 9], 'type_indices_not_consumed': [10, 18]},
    'max_absolute_error_against_paddle': max(errors),
    'tested_random_inputs': len(errors),
}
(root / (stem + '.conversion-report.json')).write_text(json.dumps(report, indent=2), encoding='utf-8')
print(json.dumps(report, indent=2))
