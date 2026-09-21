"""Create the CPU INT8 YuNet package source from the preserved FP32 model.

Use representative camera crops instead of synthetic calibration data for a
production release. The source FP32 model is never modified or deleted.
"""

from pathlib import Path
import sys

import numpy as np
import onnx
from onnxruntime.quantization import CalibrationDataReader, CalibrationMethod, QuantFormat, QuantType, quantize_static


class CalibrationReader(CalibrationDataReader):
    def __init__(self, input_name: str, count: int = 64) -> None:
        rng = np.random.default_rng(42)
        self._batches = iter(
            {input_name: rng.uniform(0.0, 1.0, (1, 3, 640, 640)).astype(np.float32)}
            for _ in range(count)
        )

    def get_next(self):
        return next(self._batches, None)


def main() -> None:
    root = Path(__file__).resolve().parent.parent
    source = root / "RawModels" / (sys.argv[1] if len(sys.argv) > 1 else "face_detection_yunet_2023mar.onnx")
    destination = root / "RawModels" / (sys.argv[2] if len(sys.argv) > 2 else "face_detection_yunet_2023mar_int8.onnx")
    if not source.is_file():
        raise FileNotFoundError(source)
    model = onnx.load(source, load_external_data=False)
    input_name = model.graph.input[0].name
    quantize_static(
        model_input=str(source), model_output=str(destination),
        calibration_data_reader=CalibrationReader(input_name),
        quant_format=QuantFormat.QDQ,
        activation_type=QuantType.QUInt8, weight_type=QuantType.QInt8,
        # Per-tensor weights keep the package compatible with the ONNX
        # operator set used by the desktop ONNX Runtime build.
        per_channel=False, reduce_range=False,
        calibrate_method=CalibrationMethod.MinMax,
        op_types_to_quantize=["Conv", "MatMul"],
    )
    print(destination)


if __name__ == "__main__":
    sys.exit(main())
