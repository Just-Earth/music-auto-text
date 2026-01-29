import sys
import json
import argparse

parser = argparse.ArgumentParser()
parser.add_argument('--audio', '-a', required=True)
parser.add_argument('--text', '-t', required=True)
parser.add_argument('--model', '-m', default='small')
parser.add_argument('--device', '-d', default='cpu')
args = parser.parse_args()

try:
    import whisper
    import torch
except Exception as e:
    print(json.dumps({"error": "import_failed", "message": str(e)}))
    sys.exit(2)

# Check if whisperx is available for better alignment
try:
    import whisperx
    HAS_WHISPERX = True
except ImportError:
    HAS_WHISPERX = False

audio = args.audio
text_path = args.text
model_name = args.model
device = args.device

def load_text_file(path):
    """Load text file and return lines as list."""
    with open(path, 'r', encoding='utf-8') as f:
        content = f.read()
    lines = [l.strip() for l in content.split('\n') if l.strip()]
    return lines

def align_with_whisperx(audio_path, text_lines, model_name, device):
    """Use whisperx for forced alignment of provided text to audio."""
    # First transcribe to get audio features
    try:
        model = whisper.load_model(model_name, device=device)
    except TypeError:
        model = whisper.load_model(model_name)
    
    # Create segments from provided text (not from transcription!)
    # We'll create fake segments covering the whole audio, then let whisperx align them
    full_text = " ".join(text_lines)
    
    # Get audio duration by loading audio
    import whisper.audio as wa
    audio_array = wa.load_audio(audio_path)
    duration = len(audio_array) / wa.SAMPLE_RATE
    
    # Create a single segment spanning the whole audio with the provided text
    # WhisperX will align words within this segment
    segments = [{
        "start": 0.0,
        "end": duration,
        "text": full_text
    }]
    
    # Load alignment model
    try:
        model_a, metadata = whisperx.load_align_model(language_code="en", device=device)
    except TypeError:
        try:
            model_a, metadata = whisperx.load_align_model(device=device)
        except TypeError:
            model_a, metadata = whisperx.load_align_model()
    
    # Perform alignment with the provided text
    try:
        result_aligned = whisperx.align(segments, model_a, metadata, audio_path, device=device, return_word_timestamps=True)
    except TypeError:
        result_aligned = whisperx.align(segments, model_a, metadata, audio_path, return_word_timestamps=True)
    
    words = []
    if isinstance(result_aligned, dict) and "word_segments" in result_aligned:
        for w in result_aligned["word_segments"]:
            words.append({
                "start": w.get('start', 0.0),
                "end": w.get('end', 0.0),
                "word": w.get('word', '')
            })
    elif isinstance(result_aligned, dict) and "segments" in result_aligned:
        # Some versions return segments with words inside
        for seg in result_aligned["segments"]:
            for w in seg.get("words", []):
                words.append({
                    "start": w.get('start', 0.0),
                    "end": w.get('end', 0.0),
                    "word": w.get('word', '')
                })
    
    return words

def align_with_whisper_only(audio_path, text_lines, model_name, device):
    """Fallback: use whisper transcription and match words to provided text."""
    try:
        model = whisper.load_model(model_name, device=device)
    except TypeError:
        model = whisper.load_model(model_name)
    
    # Transcribe with word timestamps if available
    result = model.transcribe(audio_path, word_timestamps=True)
    
    # Extract word-level timestamps from transcription
    transcribed_words = []
    for seg in result.get('segments', []):
        # Check if segment has word-level timestamps
        if 'words' in seg:
            for w in seg['words']:
                transcribed_words.append({
                    "start": w.get('start', 0.0),
                    "end": w.get('end', 0.0),
                    "word": w.get('word', '').strip()
                })
        else:
            # Fallback: split segment text evenly
            seg_text = seg.get('text', '').strip()
            seg_start = seg.get('start', 0.0)
            seg_end = seg.get('end', 0.0)
            seg_words = seg_text.split()
            if len(seg_words) == 0:
                continue
            dur = seg_end - seg_start
            for i, w in enumerate(seg_words):
                start = seg_start + dur * i / len(seg_words)
                end = seg_start + dur * (i + 1) / len(seg_words)
                transcribed_words.append({"start": start, "end": end, "word": w})
    
    # Now match provided text lines to transcribed words using fuzzy matching
    provided_words = []
    for line in text_lines:
        for w in line.split():
            provided_words.append(w.strip())
    
    # Simple alignment: try to find best match for each provided word in sequence
    aligned_words = []
    trans_idx = 0
    
    def normalize(s):
        """Normalize word for comparison."""
        import re
        return re.sub(r'[^\w]', '', s.lower())
    
    for pw in provided_words:
        pw_norm = normalize(pw)
        if not pw_norm:
            continue
        
        best_match = None
        best_score = 0
        search_range = min(20, len(transcribed_words) - trans_idx)  # Look ahead up to 20 words
        
        for offset in range(search_range):
            if trans_idx + offset >= len(transcribed_words):
                break
            tw = transcribed_words[trans_idx + offset]
            tw_norm = normalize(tw['word'])
            
            # Check for exact match or high similarity
            if pw_norm == tw_norm:
                best_match = tw
                best_score = 1.0
                trans_idx = trans_idx + offset + 1
                break
            elif pw_norm in tw_norm or tw_norm in pw_norm:
                score = 0.8 - offset * 0.02
                if score > best_score:
                    best_match = tw
                    best_score = score
        
        if best_match:
            aligned_words.append({
                "start": best_match['start'],
                "end": best_match['end'],
                "word": pw
            })
        else:
            # No match found - estimate position based on previous word
            if aligned_words:
                last = aligned_words[-1]
                aligned_words.append({
                    "start": last['end'],
                    "end": last['end'] + 0.3,
                    "word": pw
                })
            else:
                aligned_words.append({
                    "start": 0.0,
                    "end": 0.3,
                    "word": pw
                })
    
    return aligned_words

try:
    text_lines = load_text_file(text_path)
    
    if HAS_WHISPERX:
        # Try whisperx forced alignment first
        try:
            words = align_with_whisperx(audio, text_lines, model_name, device)
            if not words:
                raise Exception("WhisperX returned no words")
        except Exception as e:
            # Fallback to whisper-only alignment
            words = align_with_whisper_only(audio, text_lines, model_name, device)
    else:
        # Use whisper-only alignment
        words = align_with_whisper_only(audio, text_lines, model_name, device)
    
    print(json.dumps({"words": words}))
    
except Exception as e:
    import traceback
    print(json.dumps({"error": "alignment_failed", "message": str(e), "trace": traceback.format_exc()}))
    sys.exit(3)
