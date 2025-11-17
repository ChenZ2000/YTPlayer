import type { ITokenizer } from 'strtok3';
export declare class GzipHandler {
    private tokenizer;
    private gunzip;
    constructor(tokenizer: ITokenizer);
    inflate(): ReadableStream<Uint8Array>;
}
