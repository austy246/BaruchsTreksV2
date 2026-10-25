namespace BaruchsTreks.Http;

/// <summary>Tělo požadavku přesáhlo <see cref="RequestBodyLimit.MaxBytes"/>.</summary>
/// <remarks>
/// Záměrně není <see cref="IOException"/> ani <see cref="InvalidDataException"/>: antiforgery i čtení formuláře
/// v MVC tyto dva typy zabalí do chyby 400 a 413 by se ztratilo.
/// </remarks>
public sealed class RequestBodyTooLargeException() : Exception("Request body is too large.");

/// <summary>
/// Limit těla požadavku 50 MB. Kestrel ho vynucuje sám (<c>MaxRequestBodySize</c>), ale TestServer ne
/// a Kestrel vyhodí <c>BadHttpRequestException</c>, kterou by antiforgery přeměnil na 400. Proto limit hlídá
/// i tento middleware a vrací 413.
/// </summary>
public static class RequestBodyLimit
{
    public const long MaxBytes = 52_428_800;

    public static IApplicationBuilder UseRequestBodyLimit(this IApplicationBuilder app) =>
        app.Use(async (context, next) =>
        {
            var request = context.Request;
            if (request.ContentLength > MaxBytes)
            {
                await RejectAsync(context);
                return;
            }

            if (!HttpMethods.IsGet(request.Method) && !HttpMethods.IsHead(request.Method))
            {
                request.Body = new LimitedBodyStream(request.Body, MaxBytes);
            }

            try
            {
                await next(context);
            }
            catch (RequestBodyTooLargeException) when (!context.Response.HasStarted)
            {
                await RejectAsync(context);
            }
        });

    private static Task RejectAsync(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        context.Response.ContentType = "text/plain; charset=utf-8";
        return context.Response.WriteAsync("Request body is too large (limit is 50 MB).");
    }

    /// <summary>Čte z původního těla a po překročení limitu vyhodí <see cref="RequestBodyTooLargeException"/>.</summary>
    private sealed class LimitedBodyStream(Stream inner, long limit) : Stream
    {
        private long _total;

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Count(ReadInner(() => inner.Read(buffer, offset, count)));

        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try
            {
                return Count(await inner.ReadAsync(buffer, cancellationToken));
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                throw new RequestBodyTooLargeException();
            }
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
            ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        private static int ReadInner(Func<int> read)
        {
            try
            {
                return read();
            }
            catch (BadHttpRequestException ex) when (ex.StatusCode == StatusCodes.Status413PayloadTooLarge)
            {
                throw new RequestBodyTooLargeException();
            }
        }

        private int Count(int read)
        {
            _total += read;
            return _total > limit ? throw new RequestBodyTooLargeException() : read;
        }
    }
}
