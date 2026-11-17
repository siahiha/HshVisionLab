"""Convert Intel OMZ 0039 IR to ONNX, validating every op against a Torch IR interpreter.
Usage: python convert_vehicle_color.py RAW_MODEL_DIRECTORY
Dependencies: numpy, onnx, onnxruntime, torch. No training or remote code execution.
"""
import hashlib
import json
import sys
import xml.etree.ElementTree as ET
from pathlib import Path
import numpy as np
import onnx
from onnx import helper as h, numpy_helper as nh
import onnxruntime as ort
import torch
import torch.nn.functional as F

root = Path(sys.argv[1])
stem = 'vehicle-attributes-recognition-barrier-0039'
xml, weights = root / (stem + '.xml'), root / (stem + '.bin')
tree = ET.parse(xml)
blob = weights.read_bytes()
assert hashlib.sha384(xml.read_bytes()).hexdigest() == '9cafca52c680cb9f0eee61dee93465e73862e7625729da5657c09ac89b12b8a4718abc894a7a111b05931bc7999ab20c', 'Unexpected source XML'
assert hashlib.sha384(blob).hexdigest() == 'b3b6fcdfdf5d42913f5461ea35f90e886c4cb18441ae3438562a962b59d64795427a6401cd2321c6047917f7d5bf6e97', 'Unexpected source weights'
layers = {int(l.get('id')): l for l in tree.findall('./layers/layer')}
edges = {}
for e in tree.findall('./edges/edge'):
    edges.setdefault(int(e.get('to-layer')), []).append((int(e.get('to-port')), int(e.get('from-layer'))))
nodes, constants, outputs, operations = [], [], [], []
values = {}
def ints(v): return [int(x.strip()) for x in v.split(',')]
def name(i): return 'input' if layers[i].get('type') == 'Parameter' else 'v' + str(i)
for i, layer in sorted(layers.items()):
    kind = layer.get('type')
    d = layer.find('data')
    d = d.attrib if d is not None else {}
    ins = [j for _, j in sorted(edges.get(i, []))]
    if kind == 'Parameter': continue
    if kind == 'Const':
        dtype = {'f32': np.float32, 'i64': np.int64}[d['element_type']]
        a = np.frombuffer(blob, dtype=dtype, count=int(d['size']) // np.dtype(dtype).itemsize, offset=int(d['offset'])).copy().reshape(ints(d['shape']))
        values[i] = torch.from_numpy(a)
        constants.append(nh.from_array(a, name(i)))
        continue
    if kind == 'Result':
        output_name = layers[ins[0]].get('name')
        shape = [int(x.text) for x in layer.findall('./input/port/dim')]
        nodes.append(h.make_node('Identity', [name(ins[0])], [output_name]))
        outputs.append(h.make_tensor_value_info(output_name, onnx.TensorProto.FLOAT, shape))
        continue
    attrs = {}
    op = {'Multiply': 'Mul', 'Add': 'Add', 'ReLU': 'Relu', 'Convolution': 'Conv', 'MaxPool': 'MaxPool', 'ReduceMean': 'ReduceMean', 'SoftMax': 'Softmax'}[kind]
    inputs = [name(j) for j in ins]
    if kind in ['Convolution', 'MaxPool']:
        assert d['auto_pad'] == 'explicit'
        attrs.update(strides=ints(d['strides']), pads=ints(d['pads_begin']) + ints(d['pads_end']), dilations=ints(d['dilations']))
    if kind == 'MaxPool': attrs.update(kernel_shape=ints(d['kernel']), ceil_mode=int(d['rounding_type'] == 'ceil'))
    if kind == 'ReduceMean':
        attrs.update(axes=values[ins[1]].tolist(), keepdims=int(d['keep_dims'] == 'true'))
        inputs = inputs[:1]
    if kind == 'SoftMax': attrs['axis'] = int(d['axis'])
    nodes.append(h.make_node(op, inputs, [name(i)], name=layer.get('name'), **attrs))
    operations.append((i, kind, ins, attrs))
used_inputs = {name for node in nodes for name in node.input}
constants = [c for c in constants if c.name in used_inputs]
model = h.make_model(h.make_graph(nodes, stem, [h.make_tensor_value_info('input', onnx.TensorProto.FLOAT, [1,3,72,72])], outputs, constants), opset_imports=[h.make_opsetid('', 13)])
model.ir_version = 8
onnx.checker.check_model(model)
path = root / (stem + '.onnx')
onnx.save(model, path)
options = ort.SessionOptions(); options.intra_op_num_threads = 1
session = ort.InferenceSession(str(path), options, providers=['CPUExecutionProvider'])
torch.set_num_threads(1)
errors = []
for seed in range(5):
    array = np.random.default_rng(seed).uniform(0,255,(1,3,72,72)).astype(np.float32)
    v = dict(values); v[0] = torch.from_numpy(array)
    for i, kind, ins, a in operations:
        x = [v[j] for j in ins]
        if kind == 'Multiply': y = x[0] * x[1]
        elif kind == 'Add': y = x[0] + x[1]
        elif kind == 'ReLU': y = F.relu(x[0])
        elif kind == 'Convolution':
            assert a['pads'][:2] == a['pads'][2:]
            y = F.conv2d(x[0], x[1], stride=a['strides'], padding=a['pads'][:2], dilation=a['dilations'])
        elif kind == 'MaxPool': y = F.max_pool2d(x[0], a['kernel_shape'], a['strides'], a['pads'][:2], a['dilations'], bool(a['ceil_mode']))
        elif kind == 'ReduceMean': y = x[0].mean(tuple(a['axes']), keepdim=bool(a['keepdims']))
        elif kind == 'SoftMax': y = x[0].softmax(a['axis'])
        v[i] = y
    expected = [v[sorted(edges[int(l.get('id'))])[0][1]].numpy() for l in sorted(layers.values(), key=lambda l:int(l.get('id'))) if l.get('type') == 'Result']
    actual = session.run(None, {'input': array})
    for ref, out in zip(expected, actual):
        err = float(np.max(np.abs(ref-out))); errors.append(err)
        assert np.allclose(ref,out,atol=2e-5,rtol=2e-4), err
report = {'source_sha256': {p.name:hashlib.sha256(p.read_bytes()).hexdigest() for p in [xml, weights]}, 'onnx_sha256':hashlib.sha256(path.read_bytes()).hexdigest(), 'max_absolute_error_against_torch_ir':max(errors), 'tested_random_inputs':5}
(root/'conversion-report.json').write_text(json.dumps(report,indent=2))
print(json.dumps(report,indent=2))
