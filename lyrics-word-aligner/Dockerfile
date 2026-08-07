FROM nvidia/cuda:12.8.1-cudnn-runtime-ubuntu24.04

ENV DEBIAN_FRONTEND=noninteractive \
    PYTHONUNBUFFERED=1 \
    PIP_NO_CACHE_DIR=1 \
    HF_HOME=/models/huggingface \
    MODEL_CACHE=/models

RUN apt-get update && apt-get install -y --no-install-recommends \
    python3 python3-pip python3-venv python3-dev \
    build-essential gcc g++ git \
    ffmpeg libsndfile1 curl espeak-ng \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
RUN python3 -m pip install --break-system-packages \
    torch torchvision torchaudio --index-url https://download.pytorch.org/whl/cu128

# Keep the large CUDA/PyTorch layer reusable when a smaller Python dependency
# changes during aligner development.
COPY requirements.txt .
RUN python3 -m pip install --break-system-packages -r requirements.txt \
    && python3 -m pip install --break-system-packages --no-deps easyaligner==0.3.3 \
    && python3 -c "import torch, onnxruntime as ort, transformers; from transformers import Qwen3ASRForTokenClassification; print('Torch:', torch.__version__, 'CUDA:', torch.version.cuda); print('Transformers:', transformers.__version__); print('Qwen3ASRForTokenClassification: OK'); print('ONNX Runtime:', ort.__version__, ort.get_available_providers())" \
    && apt-get purge -y --auto-remove build-essential gcc g++ git python3-dev \
    && rm -rf /var/lib/apt/lists/* /root/.cache/pip

COPY app ./app
COPY third_party/SOFA ./SOFA
RUN mkdir -p /data/output /models && chmod -R 777 /data /models
EXPOSE 8080
HEALTHCHECK --interval=30s --timeout=5s --start-period=15s --retries=3 \
  CMD curl -fsS http://localhost:8080/health || exit 1
CMD ["python3", "-m", "uvicorn", "app.main:app", "--host", "0.0.0.0", "--port", "8080"]
