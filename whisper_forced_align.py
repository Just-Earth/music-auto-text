import sys
import json
import argparse

parser = argparse.ArgumentParser()
parser.add_argument('--audio', '-a', required=True)
parser.add_argument('--text', '-t', required=True)
parser.add_argument('--model', '-m', default='small')
args = parser.parse_args()

try:
    import whisper
    import whisperx
except Exception as e:
    print(json.dumps({"error":"import_failed","message":str(e)}))
    sys.exit(2)

audio = args.audio
text_path = args.text
model_name = args.model

try:
    model = whisper.load_model(model_name)
    result = model.transcribe(audio)
    # load alignment model
    device = "cpu"
    model_a, metadata = whisperx.load_align_model(device)
    # perform alignment
    result_aligned = whisperx.align(result["segments"], model_a, metadata, audio, return_word_timestamps=True)
    # result_aligned contains 'word_segments' key with word timestamps in whisperx API
    words = []
    if isinstance(result_aligned, dict) and "word_segments" in result_aligned:
        for w in result_aligned["word_segments"]:
            words.append({"start": w.get('start',0.0), "end": w.get('end',0.0), "word": w.get('word','')})
    else:
        # fallback: split segments evenly into words
        for seg in result.get('segments', []):
            seg_text = seg.get('text','').strip()
            seg_start = seg.get('start',0.0)
            seg_end = seg.get('end',0.0)
            seg_words = seg_text.split()
            if len(seg_words)==0: continue
            dur = seg_end - seg_start
            for i,w in enumerate(seg_words):
                start = seg_start + dur*(i)/len(seg_words)
                end = seg_start + dur*(i+1)/len(seg_words)
                words.append({"start": start, "end": end, "word": w})

    # write words JSON
    print(json.dumps({"words": words}))
except Exception as e:
    print(json.dumps({"error":"alignment_failed","message":str(e)}))
    sys.exit(3)
