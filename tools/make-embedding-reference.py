import json, sys, numpy as np, onnxruntime as ort
from tokenizers import Tokenizer
M = sys.argv[1]; out = sys.argv[2]
tok = Tokenizer.from_file(M + "/tokenizer.json")
texts = [
 "task: search result | query: Which planet is known as the Red Planet?",
 "title: none | text: Venus is often called Earth's twin because of its similar size and proximity.",
 "title: none | text: Mars, known for its reddish appearance, is often referred to as the Red Planet.",
 "title: none | text: Jupiter, the largest planet in our solar system, has a prominent red spot.",
 "title: none | text: Saturn, famous for its rings, is sometimes mistaken for the Red Planet.",
 "title: Lejekontrakt_Nørrebrogade_2024.pdf | text: Depositum svarende til 3 måneders husleje, i alt 42.000 kr., betales ved underskrift.",
 "Hello   world\n\tTabs and  double  spaces — em-dash, emoji-free, 日本語テキスト, café",
]
enc = [tok.encode(t) for t in texts]
ids = [e.ids for e in enc]
s = ort.InferenceSession(M + "/onnx/model_quantized.onnx")
embs = []
for i in ids:
    a = np.array([i], dtype=np.int64)
    _, e = s.run(None, {"input_ids": a, "attention_mask": np.ones_like(a)})
    embs.append(e[0].tolist())
q = np.array(embs[0]); docs = np.array(embs[1:5])
json.dump({"model": "onnx/model_quantized.onnx", "texts": texts, "token_ids": ids,
           "embeddings": embs, "readme_similarities": (docs @ q).tolist()}, open(out, "w"))
print("first ids", ids[0][:8], "...", ids[0][-3:]); print("sims", (docs @ q).tolist()); print("norm", float(np.linalg.norm(q)))
