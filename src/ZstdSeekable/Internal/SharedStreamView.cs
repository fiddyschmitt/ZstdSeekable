using System;
using System.IO;

namespace ZstdSeekable.Internal
{
    /// <summary>
    /// A read-only cursor over a shared stream. Each view keeps its own position; the underlying
    /// stream's position is only touched inside the shared lock, so any number of views can read
    /// the same source concurrently without corrupting each other's progress.
    /// </summary>
    internal sealed class SharedStreamView(Stream source, object gate) : Stream
    {
        long position;

        public override int Read(byte[] buffer, int offset, int count)
        {
            lock (gate)
            {
                source.Position = position;
                var read = source.Read(buffer, offset, count);
                position += read;
                return read;
            }
        }

        public override long Length
        {
            get { lock (gate) return source.Length; }
        }

        public override long Position
        {
            get => position;
            set => position = value;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => position + offset,
                SeekOrigin.End => Length + offset,
                _ => throw new ArgumentOutOfRangeException(nameof(origin)),
            };
            return position;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
