export interface Folder {
  name: string;
  finalize(survived: Uint8Array): Float32Array | Uint16Array;
}
