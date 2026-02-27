using System;
using System.Globalization;

namespace JSQ.Core.Models;

public static class JsqClock
{
    // DateTime.UtcNow + hardcoded offset создавал DateTime с Kind=Utc,
    // но со значением местного времени (семантически неверно).
    // NowIso() с форматом "O" добавлял суффикс Z → любой парсер (Dapper,
    // DB Browser, OxyPlot) считал это UTC и конвертировал в local ещё раз,
    // давая двойное смещение и неправильное время в БД и графиках.
    //
    // Решение: DateTime.Now (Kind=Local) — CLR использует часовой пояс
    // Windows, хардкод не нужен, работает на любой машине.
    // NowIso() без суффикса часового пояса — SQLite-совместимый формат,
    // читается как Kind=Unspecified (без авто-конвертации).
    public static DateTime Now => DateTime.Now;

    public static string NowIso() =>
        Now.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}
