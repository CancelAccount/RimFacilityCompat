using Verse;

namespace FacilityCompat
{
    /// <summary>
    /// 统一的日志输出工具。
    /// 因 Mod 构造函数调用时语言系统可能未就绪，全部翻译均带降级兜底。
    /// </summary>
    public static class FCLogger
    {
        private static string Prefix
        {
            get
            {
                var result = SafeTranslate("FC.LogPrefix");
                return (!string.IsNullOrEmpty(result) && result != "FC.LogPrefix")
                    ? result : "[FacilityCompat]";
            }
        }

        // 普通消息，key 为翻译 key
        /// <summary>直接输出英文日志（不经过翻译），用于启动阶段诊断</summary>
        public static void Raw(string msg)
        {
            Log.Message($"{Prefix} {msg}");
        }

        /// <summary>格式化日志：翻译成功则用模板替换，失败则输出 "key(arg1, arg2)"</summary>
        private static string FormatMsg(string key, params object[] args)
        {
            var translated = SafeTranslate(key);
            if (args.Length > 0 && translated == key)
                return $"{translated}({string.Join(", ", args)})";
            return string.Format(translated, args);
        }

        public static void Msg(string key, params object[] args)
        {
            Log.Message($"{Prefix} {FormatMsg(key, args)}");
        }

        public static void Warn(string key, params object[] args)
        {
            Log.Warning($"{Prefix} {FormatMsg(key, args)}");
        }

        public static void Err(string key, params object[] args)
        {
            Log.Error($"{Prefix} {FormatMsg(key, args)}");
        }

        // 异常错误，msg 为已翻译文本
        public static void Exception(string msg, System.Exception ex)
        {
            Log.Error($"{Prefix} {msg}: {ex}");
        }

        /// <summary>安全翻译：语言系统未就绪时降级返回 key 本身</summary>
        private static string SafeTranslate(string key)
        {
            try
            {
                if (LanguageDatabase.activeLanguage != null)
                {
                    var result = key.Translate();
                    if (result.ToString() != key)
                        return result;
                }
            }
            catch { }
            return key;
        }
    }
}
