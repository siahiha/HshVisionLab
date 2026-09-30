# Palm models

The Palm module accepts external ONNX files. Put the files beside the deployed
application under `Models/Palm` (or under this folder during a development run):

| File | Role | Expected input/output |
| --- | --- | --- |
| `palm_blazepalm_full.onnx` | fast palm localization | `1x3x192x192`, MediaPipe/BlazePalm raw SSD outputs |
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

ONNX binaries are deliberately not committed to this repository: the existing
repository policy ignores model binaries, and the CCNet package is large. The
runtime reports the missing model path in the module availability status until
the selected files are installed.
