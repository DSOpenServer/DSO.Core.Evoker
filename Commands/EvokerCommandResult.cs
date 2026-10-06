using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;
using DSO.Core.Evoker.Conversion;

namespace DSO.Core.Evoker.Commands
{
    /// <summary>
    /// Komut sonucu - web'e doğrudan JSON olarak verilebilecek şekilde:
    /// <code>
    /// { "success": true, "result": 7, "elapsedMs": 0.08 }                                   // tek adım
    /// { "success": true, "steps": [ { "index":0, "op":"set", ... }, ... ], "elapsedMs": 412.6 } // çok adım
    /// { "success": false, "error": { "code": "TargetException", "message": "...", "exceptionType": "...", "stepIndex": 1 } }
    /// </code>
    /// Result: in-process'te gerçek CLR nesnesi; process sınırını geçtiyse (sandbox) JsonElement. İkisi de aynı JSON'a yazılır.
    /// </summary>
    public sealed class EvokerCommandResult
    {
        public bool Success { get; set; }
        public object? Result { get; set; }
        public List<EvokerStepResult>? Steps { get; set; }
        public EvokerError? Error { get; set; }
        public double ElapsedMs { get; set; }
        /// <summary>Plugin hedeflerinde "InProcess" / "Sandbox"; diğerlerinde null.</summary>
        public string? Mode { get; set; }

        public static EvokerCommandResult Fail(string code, string message, string? exceptionType = null, int? stepIndex = null, string? member = null) =>
            new() { Success = false, Error = new EvokerError { Code = code, Message = message, ExceptionType = exceptionType, StepIndex = stepIndex, Member = member } };

        public string ToJson(bool indented = false) => JsonSerializer.Serialize(this, indented ? IndentedOptions : Options);

        public static EvokerCommandResult FromJson(string json) =>
            JsonSerializer.Deserialize<EvokerCommandResult>(json, Options) ?? throw new JsonException("Boş sonuç.");

        /// <summary>Sonuç yazımı için ayarlar (camelCase zarf, null alanlar yazılmaz, Türkçe karakterler kaçışsız).</summary>
        public static JsonSerializerOptions Options => _options ??= Create(false);
        private static JsonSerializerOptions IndentedOptions => _indented ??= Create(true);
        private static JsonSerializerOptions? _options, _indented;

        private static JsonSerializerOptions Create(bool indented)
        {
            var o = new JsonSerializerOptions(EvokerJson.Output)
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                PropertyNameCaseInsensitive = true,
                DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
                WriteIndented = indented
            };
            return o;
        }

        /// <summary>Plugin unload'u: sonuç ayarları plugin tiplerinin metadata'sını cache'lemiş olabilir.</summary>
        internal static void ResetOptions() { _options = null; _indented = null; }
    }

    public sealed class EvokerStepResult
    {
        public int Index { get; set; }
        public string Op { get; set; } = "";
        public string? Member { get; set; }
        public string? As { get; set; }
        public bool Success { get; set; }
        public object? Result { get; set; }
        public EvokerError? Error { get; set; }
        public double ElapsedMs { get; set; }
        /// <summary>Çalıştırılmadı (önceki adım hata verdi ve StopOnError=true).</summary>
        public bool? Skipped { get; set; }
    }

    public sealed class EvokerError
    {
        /// <summary>Bkz. <see cref="EvokerErrorCodes"/>.</summary>
        public string Code { get; set; } = "";
        public string Message { get; set; } = "";
        /// <summary>Hedef kodun fırlattığı exception'ın tam tip adı (Code=TargetException iken).</summary>
        public string? ExceptionType { get; set; }
        public int? StepIndex { get; set; }
        /// <summary>batch'te hata veren argüman setinin sırası (öncekiler çalıştı).</summary>
        public int? BatchIndex { get; set; }
        public string? Member { get; set; }
    }

    /// <summary>Hata kodları ve önerilen HTTP karşılıkları.</summary>
    public static class EvokerErrorCodes
    {
        /// <summary>Komut bozuk / eksik (400).</summary>
        public const string BadRequest = "BadRequest";
        /// <summary>Hedef (katalog anahtarı / tip adı) yok (404).</summary>
        public const string TargetNotFound = "TargetNotFound";
        /// <summary>Metot / property / field yok (404).</summary>
        public const string MemberNotFound = "MemberNotFound";
        /// <summary>Argümanlar hiçbir overload'a uymuyor ya da dönüştürülemiyor (422).</summary>
        public const string InvalidArguments = "InvalidArguments";
        /// <summary>Birden fazla overload eşit uyuyor - argTypes ile belirtin (422).</summary>
        public const string AmbiguousMatch = "AmbiguousMatch";
        /// <summary>İşlem bu hedefte yapılamaz (ör. Static hedefte instance üyesi, salt okunur üyeye yazma) (422).</summary>
        public const string InvalidOperation = "InvalidOperation";
        /// <summary>Hedefin kendi kodu exception fırlattı (500).</summary>
        public const string TargetException = "TargetException";
        /// <summary>Bekleme süresi doldu (504).</summary>
        public const string Timeout = "Timeout";
        /// <summary>Erişim izni yok (ör. izin listesinde olmayan tip) (403).</summary>
        public const string NotAllowed = "NotAllowed";
        /// <summary>Hedef şu an kullanıma kapalı (ör. pasif plugin) (409).</summary>
        public const string Inactive = "Inactive";
        /// <summary>Çağıran iptal etti (ör. HTTP isteği kapandı) (499).</summary>
        public const string Cancelled = "Cancelled";
        /// <summary>Beklenmeyen iç hata (500).</summary>
        public const string InternalError = "InternalError";

        /// <summary>Hata kodunun önerilen HTTP durum kodu.</summary>
        public static int HttpStatus(string? code) => code switch
        {
            BadRequest => 400,
            NotAllowed => 403,
            TargetNotFound or MemberNotFound => 404,
            Inactive => 409,
            InvalidArguments or AmbiguousMatch or InvalidOperation => 422,
            Timeout => 504,
            Cancelled => 499,
            _ => 500
        };
    }

    /// <summary>Komut çalıştırma hatası (kodlu). Çalıştırıcı bunu EvokerCommandResult'a çevirir.</summary>
    public sealed class EvokerCommandException : Exception
    {
        public string Code { get; }
        public string? TargetExceptionType { get; init; }
        public int? BatchIndex { get; init; }

        public EvokerCommandException(string code, string message, Exception? inner = null) : base(message, inner) => Code = code;
    }
}