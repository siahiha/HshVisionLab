# Palm models

The Palm module accepts external ONNX files. Put the files beside the deployed
application under `Models/Palm` (or under this folder during a development run):

| File | Role | Expected input/output |
| --- | --- | --- |
| `palm_blazepalm_full.onnx` | fast palm localization | actual graph input `1x192x192x3` (NHWC); MediaPipe/BlazePalm raw SSD outputs |
| `rtmdet_nano_hand.onnx` | alternative localization | RTMDet-nano hand model, 320x320 |
| `palm_ccnet.onnx` | palm identity embedding | grayscale `1x1x128x128` -> embedding (CCNet is commonly 2048-D) |
| `palm_ppnet.onnx` | alternative palm identity embedding | grayscale `1x1x128x128` -> embedding |

The defaults select BlazePalm plus CCNet. The model can be changed from the
Palm processing options without changing code. PPNet is supported as a generic
embedding model when its exported ONNX graph follows the same input contract.

Recommended upstream sources:

- BlazePalm ONNX conversion: <https://github.com/yakhyo/mediapipe-hand-landmark-onnx>
- OpenMMLab RTMDet-nano hand export: <https://download.openmmlab.com/mmpose/v1/projects/rtmposev1/onnx_sdk/rtmdet_nano_8xb32-300e_hand-267f9c8f.zip>
- CCNet ONNX feature extractor: <https://huggingface.co/kyereboatengcaleb/palm-ccnet-onnx>
- PPNet reference implementation: <https://github.com/xuliangcs/ppnet>

The two detector ONNX files are included for the Windows/Service runtime. The
large `palm_ccnet.onnx` recognition file is installed separately and is ignored
by the repository. If it is missing, Palm detection remains available but Palm
identity recognition and enrollment are unavailable. `palm_ppnet.onnx` is an
optional external model with the same grayscale `1x1x128x128` contract.
