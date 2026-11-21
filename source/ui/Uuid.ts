type FillRandomBytes = (bytes: Uint8Array<ArrayBuffer>) => void;

// getRandomValues remains available for browser clients on an HTTP LAN origin.
export function newUuid(
  fillRandomBytes: FillRandomBytes = (bytes) => {
    crypto.getRandomValues(bytes);
  },
): string {
  const bytes = new Uint8Array(new ArrayBuffer(16));
  fillRandomBytes(bytes);
  bytes[6] = (bytes[6]! & 15) | 64;
  bytes[8] = (bytes[8]! & 63) | 128;
  const id = Array.from(bytes, (byte) => byte.toString(16).padStart(2, "0")).join("");
  return `${id.slice(0, 8)}-${id.slice(8, 12)}-${id.slice(12, 16)}-${id.slice(16, 20)}-${id.slice(20)}`;
}
