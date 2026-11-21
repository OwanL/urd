// Only remove the transport header when it identifies this exact background call.
export function subagentReportText(output: string, returned: boolean, callId: string): string {
  if (!returned) return output;
  // ToolResultCodec encodes control characters in header fields as JSON text.
  const id = /[\x00-\x1f\x7f\u2028\u2029]/u.test(callId)
    ? JSON.stringify(callId).slice(1, -1)
    : callId;
  const prefix = `Results from background subagent call, ID: ${id}`;
  for (const suffix of ["\n", " (failed)\n", " (cancelled)\n"]) {
    const header = prefix + suffix;
    if (output.startsWith(header)) return output.slice(header.length);
  }
  return output;
}

export function subagentReportPreview(text: string, limit: number): string {
  let end = Math.min(limit, text.length);
  // A short character prefix can still have many lines. Keep the inline report compact.
  let lineEnd = -1;
  for (let line = 0; line < 6; line++) {
    lineEnd = text.indexOf("\n", lineEnd + 1);
    if (lineEnd === -1 || lineEnd >= end) break;
    if (line === 5) end = lineEnd;
  }
  const last = text.charCodeAt(end - 1);
  if (end < text.length && last >= 0xd800 && last <= 0xdbff) end--;
  return text.slice(0, end);
}
