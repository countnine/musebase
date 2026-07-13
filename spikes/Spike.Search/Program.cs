// M1 라이브 스모크: 실제 API에 검색을 날려 제공자 동작을 확인한다.
// 사용법: dotnet run -- [제목] [아티스트]  (기본: 夜に駆ける YOASOBI)

using LyricsX.Core.Search;

var title = args.Length > 0 ? args[0] : "夜に駆ける";
var artist = args.Length > 1 ? args[1] : "YOASOBI";

var request = LyricsSearchRequest.ByInfo(title, artist, limit: 3);
ILyricsProvider[] providers = [new LrclibProvider(), new NetEaseProvider()];

foreach (var provider in providers)
{
    Console.WriteLine($"\n===== {provider.ServiceName}: \"{title}\" / {artist} =====");
    var count = 0;
    try
    {
        await foreach (var lyrics in provider.GetLyricsAsync(request))
        {
            count++;
            var translated = lyrics.Lines.Count(l => l.Attachments.Translation() is not null);
            Console.WriteLine(
                $"[{count}] {lyrics.IdTags.GetValueOrDefault("ar")} - {lyrics.IdTags.GetValueOrDefault("ti")}" +
                $" | 라인 {lyrics.Lines.Count}개, 번역 {translated}개, length={lyrics.IdTags.GetValueOrDefault("length")}");

            var sample = lyrics.Lines.FirstOrDefault(l => l.Content.Length > 0);
            if (sample is not null)
            {
                Console.WriteLine($"    ┌ [{sample.TimeTag}] {sample.Content}");
                if (sample.Attachments.Translation() is { } tr)
                    Console.WriteLine($"    └ 번역: {tr}");
            }
        }
        Console.WriteLine(count == 0 ? "결과 없음" : $"→ {count}개 후보 취득 성공");
    }
    catch (Exception e)
    {
        Console.WriteLine($"오류: {e.Message}");
    }
}
