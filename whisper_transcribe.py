import sys
import json
import argparse

try:
    import whisper
except Exception as e:
    print(json.dumps({"error":"whisper_import_failed","message":str(e)}))
    sys.exit(2)

parser = argparse.ArgumentParser()
parser.add_argument('--input', '-i', required=True)
parser.add_argument('--model', '-m', default='small')
parser.add_argument('--device', '-d', default='cpu')
args = parser.parse_args()

audio_path = args.input
model_name = args.model
device = args.device

try:
    # whisper.load_model supports device="cuda" or device="cpu" via kwargs
    model = whisper.load_model(model_name, device=device) if 'device' in whisper.load_model.__code__.co_varnames else whisper.load_model(model_name)
    # ensure model on correct device for older APIs
    result = model.transcribe(audio_path)
    segments = result.get('segments', [])
    out = {'segments': []}
    for s in segments:
        out['segments'].append({'start': s.get('start',0.0), 'end': s.get('end',0.0), 'text': s.get('text','')})
    print(json.dumps(out))
except Exception as e:
    print(json.dumps({"error":"transcribe_failed","message":str(e)}))
    sys.exit(3)
