"""Writes tests/Valafind.Core.Tests/Fixtures/embeddinggemma-reference.json: token ids and embeddings from the reference
pipeline (Hugging Face transformers processor + Python onnxruntime on the same ONNX files Valafind uses), for text and
for the media fixtures in tests/Valafind.Core.Tests/Fixtures/Media.

    python tools/make-embedding-reference.py <model-dir> <output.json>

Needs: transformers>=5.23, onnxruntime, numpy, pillow, soundfile, av, torch, torchvision.
"""
import json
import sys
from pathlib import Path

import av
import numpy as np
import onnxruntime as ort
import soundfile as sf
from PIL import Image, ImageOps
from transformers import AutoProcessor
from transformers.video_utils import VideoMetadata

M = Path(sys.argv[1])
OUT = Path(sys.argv[2])
MEDIA = Path(__file__).resolve().parent.parent / "tests" / "Valafind.Core.Tests" / "Fixtures" / "Media"

# Valafind's defaults (EmbeddingOptions): 140 soft tokens per picture, 70 per video frame.
IMAGE_SOFT_TOKENS = 140
VIDEO_FRAME_SOFT_TOKENS = 70

proc = AutoProcessor.from_pretrained(M)
so = ort.SessionOptions()
so.intra_op_num_threads = 4
text_model = ort.InferenceSession(str(M / "onnx/model_quantized.onnx"), so)
vision = ort.InferenceSession(str(M / "onnx/vision_encoder_quantized.onnx"), so)
audio = ort.InferenceSession(str(M / "onnx/audio_encoder_quantized.onnx"), so)
EMPTY = np.zeros((0, 512), np.float32)


def embed(ids, image=EMPTY, video=EMPTY, aud=EMPTY):
    ids = np.asarray(ids, dtype=np.int64)
    return text_model.run(["sentence_embedding"], {
        "input_ids": ids, "attention_mask": np.ones_like(ids),
        "image_features": image, "video_features": video, "audio_features": aud})[0][0]


def unpadded(pixel_values, positions):
    n = int((positions[0, :, 0] >= 0).sum())
    return pixel_values[:, :n].astype(np.float32), positions[:, :n].astype(np.int64)


texts = [
    "task: search result | query: Which planet is known as the Red Planet?",
    "title: none | text: Venus is often called Earth's twin because of its similar size and proximity.",
    "title: none | text: Mars, known for its reddish appearance, is often referred to as the Red Planet.",
    "title: none | text: Jupiter, the largest planet in our solar system, has a prominent red spot.",
    "title: none | text: Saturn, famous for its rings, is sometimes mistaken for the Red Planet.",
    "title: Lejekontrakt_Nørrebrogade_2024.pdf | text: Depositum svarende til 3 måneders husleje, i alt 42.000 kr., betales ved underskrift.",
    "Hello   world\n\tTabs and  double  spaces — em-dash, emoji-free, 日本語テキスト, café",
]
token_ids = [proc.tokenizer(t)["input_ids"] for t in texts]
embeddings = [embed([i]).tolist() for i in token_ids]
q = np.array(embeddings[0])
docs = np.array(embeddings[1:5])

images = []
proc.image_processor.max_soft_tokens = IMAGE_SOFT_TOKENS
for name in ["red-circle.png", "invoice.png", "rotated.jpg"]:
    img = ImageOps.exif_transpose(Image.open(MEDIA / name)).convert("RGB")
    p = proc(images=[[img]], return_tensors="np")
    pv, pp = unpadded(p["pixel_values"], p["image_position_ids"])
    feats = vision.run(None, {"pixel_values": pv, "pixel_position_ids": pp})[0]
    images.append({
        "file": name, "width": img.width, "height": img.height,
        "patches": int(pv.shape[1]), "soft_tokens": int(feats.shape[0]),
        "pixel_values_head": pv[0, :4].reshape(-1)[:64].tolist(),
        "pixel_values_sum": float(pv.sum()),
        "input_ids": p["input_ids"][0].tolist()[:4],
        "embedding": embed(p["input_ids"], image=feats).tolist(),
    })

wav, rate = sf.read(MEDIA / "speech.wav", dtype="float32")
assert rate == 16000 and wav.ndim == 1
a = proc(audio=[wav], return_tensors="np")
feats = audio.run(None, {"input_features": a["input_features"].astype(np.float32),
                         "input_features_mask": a["input_features_mask"].astype(bool)})[0]
audio_ref = {
    "file": "speech.wav", "samples": int(len(wav)),
    "frames": int(a["input_features"].shape[1]),
    "valid_frames": int(a["input_features_mask"].sum()),
    "features_head": a["input_features"][0, :3].reshape(-1).tolist(),
    "features_sum": float(a["input_features"].sum()),
    "soft_tokens": int(feats.shape[0]),
    "embedding": embed(a["input_ids"], aud=feats).tolist(),
}

container = av.open(str(MEDIA / "clip.mp4"))
stream = container.streams.video[0]
frames = [f.to_ndarray(format="rgb24") for f in container.decode(video=0)]
fps = float(stream.average_rate)
metadata = VideoMetadata(total_num_frames=len(frames), fps=fps, duration=len(frames) / fps, video_backend="pyav",
                         width=frames[0].shape[1], height=frames[0].shape[0])
proc.video_processor.max_soft_tokens = VIDEO_FRAME_SOFT_TOKENS
v = proc(videos=[np.stack(frames)], videos_kwargs={"video_metadata": [metadata]}, return_tensors="np")
pv, pp = unpadded(v["pixel_values_videos"], v["video_position_ids"])
vfeats = np.concatenate([vision.run(None, {"pixel_values": pv[i:i + 1], "pixel_position_ids": pp[i:i + 1]})[0]
                         for i in range(pv.shape[0])])
video_ref = {
    "file": "clip.mp4", "frames": int(pv.shape[0]), "soft_tokens": int(vfeats.shape[0]),
    "embedding": embed(v["input_ids"], video=vfeats).tolist(),
}

queries = ["a red circle", "the word INVOICE written in black letters", "a weather forecast", "a colourful video test pattern with a beep"]
query_embeddings = [embed([proc.tokenizer("task: search result | query: " + t)["input_ids"]]).tolist() for t in queries]

json.dump({
    "model": "onnx-community/embeddinggemma-2-ONNX (q8)",
    "texts": texts, "token_ids": token_ids, "embeddings": embeddings,
    "readme_similarities": (docs @ q).tolist(),
    "images": images, "audio": audio_ref, "video": video_ref,
    "queries": queries, "query_embeddings": query_embeddings,
}, open(OUT, "w"))
print("sims", (docs @ q).tolist())
for im in images:
    print(im["file"], im["soft_tokens"], [round(float(np.dot(im["embedding"], e)), 3) for e in query_embeddings])
print("audio", audio_ref["soft_tokens"], [round(float(np.dot(audio_ref["embedding"], e)), 3) for e in query_embeddings])
print("video", video_ref["frames"], video_ref["soft_tokens"], [round(float(np.dot(video_ref["embedding"], e)), 3) for e in query_embeddings])
