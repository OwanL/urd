import type { AttentionKind } from "../sessions/SessionEvents.ts";

const pitches: Record<AttentionKind, readonly [number, number]> = {
  question: [740, 494],
  completion: [523, 784],
};

// An attention sound is best effort. Don't queue it while browser autoplay is locked.
export class AttentionChimes {
  private context: AudioContext | undefined;

  unlock(): void {
    try {
      this.context ??= new AudioContext();
      if (this.context.state !== "running") void this.context.resume().catch(() => undefined);
    } catch {
      // Audio support is optional.
    }
  }

  play(kind: AttentionKind): void {
    const audio = this.context;
    if (audio?.state !== "running") return;

    try {
      const start = audio.currentTime;
      pitches[kind].forEach((pitch, index) => {
        const at = start + index * 0.12;
        const end = at + 0.24;
        const tone = audio.createOscillator();
        const envelope = audio.createGain();
        tone.frequency.value = pitch;
        envelope.gain.setValueAtTime(0.001, at);
        envelope.gain.linearRampToValueAtTime(0.12, at + 0.015);
        envelope.gain.exponentialRampToValueAtTime(0.001, end);
        tone.connect(envelope);
        envelope.connect(audio.destination);
        tone.start(at);
        tone.stop(end);
      });
    } catch {
      // A device or context can become unavailable at any time.
    }
  }

  dispose(): void {
    const audio = this.context;
    this.context = undefined;
    if (audio && audio.state !== "closed") void audio.close().catch(() => undefined);
  }
}
