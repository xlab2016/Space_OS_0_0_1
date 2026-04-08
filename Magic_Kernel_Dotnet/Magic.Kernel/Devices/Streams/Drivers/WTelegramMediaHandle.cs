using System;
using System.Linq;
using Magic.Drivers.WTelegram;
using TL;

namespace Magic.Kernel.Devices.Streams.Drivers
{
    /// <summary>Opaque WTelegram media reference that keeps the open client session for lazy downloads.</summary>
    public sealed class WTelegramMediaHandle : IWeakDisposable
    {
        public required WTelegramConnection Connection { get; init; }
        public required object Media { get; set; }
        public required string Kind { get; init; }

        /// <summary>
        /// Logical size of the underlying Telegram media, when available.
        /// Exposed for scripts via GetObj(handle, "size") → handle.Size.
        /// </summary>
        public long? Size
        {
            get
            {
                // Documents: TL.MessageMediaDocument → TL.Document.size
                if (Media is MessageMediaDocument mediaDocument && mediaDocument.document is Document document)
                    return document.size;

                // Photos: best-effort – choose the largest concrete PhotoSize (if any) and return its size.
                if (Media is MessageMediaPhoto mediaPhoto && mediaPhoto.photo is Photo photo && photo.sizes != null)
                {
                    var bestSize = photo.sizes
                        .OfType<PhotoSize>()
                        .OrderByDescending(s => (long)s.w * s.h)
                        .FirstOrDefault();

                    if (bestSize != null)
                        return bestSize.size;
                }

                return null;
            }
        }

        public void WeakDispose()
        {
            // Do not dispose Connection here – it is owned by higher-level
            // connection/session lifecycle. We only drop the media reference
            // to allow GC to reclaim heavy TL objects.
            Media = null!;
        }
    }
}
