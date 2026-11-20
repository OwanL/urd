export interface SessionEventMessage {
  readonly event: string;
  readonly data: string;
}

const maxFrameLength = 64 * 1024;

export class EventStreamParser {
  private readonly dispatch: (message: SessionEventMessage) => void;
  private lineParts: string[] = [];
  private lineLength = 0;
  private frameLength = 0;
  private eventName = "";
  private dataLines: string[] = [];
  private firstLine = true;

  constructor(dispatch: (message: SessionEventMessage) => void) {
    this.dispatch = dispatch;
  }

  push(text: string): void {
    let start = 0;
    while (start < text.length) {
      const newline = text.indexOf("\n", start);
      const end = newline === -1 ? text.length : newline;
      const piece = text.slice(start, end);
      const nextLength =
        this.frameLength + this.lineLength + piece.length + (newline === -1 ? 0 : 1);
      if (nextLength > maxFrameLength) throw new Error("Session event frame exceeded 64 KB.");

      this.lineParts.push(piece);
      this.lineLength += piece.length;
      if (newline === -1) return;

      const rawLength = this.lineLength;
      let line = this.lineParts.join("");
      this.lineParts = [];
      this.lineLength = 0;
      if (line.endsWith("\r")) line = line.slice(0, -1);
      this.frameLength += rawLength + 1;
      if (line.length === 0) {
        this.finishFrame();
      } else {
        this.readLine(line);
      }
      start = newline + 1;
    }
  }

  private readLine(line: string): void {
    if (this.firstLine && line.startsWith("\uFEFF")) line = line.slice(1);
    this.firstLine = false;
    if (line.startsWith(":")) return;

    const colon = line.indexOf(":");
    const field = colon === -1 ? line : line.slice(0, colon);
    let value = colon === -1 ? "" : line.slice(colon + 1);
    if (value.startsWith(" ")) value = value.slice(1);
    if (field === "event") this.eventName = value;
    if (field === "data") this.dataLines.push(value);
  }

  private finishFrame(): void {
    const event = this.eventName || "message";
    const data = this.dataLines.join("\n");
    const hasData = this.dataLines.length > 0;
    this.eventName = "";
    this.dataLines = [];
    this.frameLength = 0;
    if (hasData) this.dispatch({ event, data });
  }
}
