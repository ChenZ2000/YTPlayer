import { AsyncGunzip } from 'fflate';
export class GzipHandler {
    constructor(tokenizer) {
        this.gunzip = undefined;
        this.tokenizer = tokenizer;
    }
    inflate() {
        let done = false;
        let cancelled = false;
        const parent = this;
        return new ReadableStream({
            start: controller => {
                parent.gunzip = new AsyncGunzip((err, chunk, final) => {
                    if (err) {
                        controller.error(err);
                        return;
                    }
                    if (chunk && !cancelled) {
                        controller.enqueue(chunk);
                    }
                    if (final && !cancelled) {
                        controller.close();
                        parent.gunzip.terminate();
                    }
                });
            },
            async pull(controller) {
                const chunkSize = 1024;
                try {
                    const buffer = new Uint8Array(chunkSize);
                    const size = await parent.tokenizer.readBuffer(buffer, { mayBeLess: true });
                    if (size === 0) {
                        if (!done) {
                            done = true;
                            if (!cancelled) {
                                parent.gunzip.push(new Uint8Array(0), true);
                            }
                        }
                        return;
                    }
                    parent.gunzip.push(buffer.subarray(0, size), false);
                }
                catch (err) {
                    controller.error(err);
                }
            },
            cancel: () => {
                parent.gunzip.terminate();
                cancelled = true;
            }
        });
    }
}
