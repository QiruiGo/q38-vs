using System;

namespace Q38Vs
{
    /// <summary>
    /// 字符串工具类。
    /// </summary>
    public static class StringUtils
    {
        /// <summary>
        /// 统计文本的行数。
        /// 空串（或 null）返回 0；否则按换行符拆分计数。
        /// 换行符同时兼容 <c>\r\n</c>、<c>\n</c>、<c>\r</c>（各算一个分隔符），
        /// 只统计非空行（即去除空行后再计数）。
        /// </summary>
        /// <param name="text">要统计的文本。</param>
        /// <returns>行数；为空或 null 时返回 0。</returns>
        public static int CountLines(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            string[] lines = text.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.RemoveEmptyEntries);
            return lines.Length;
        }

        /// <summary>
        /// 统计文本的词数。
        /// null 或空串返回 0；否则按空白字符拆分计数。
        /// 空白字符包含 <c>空格</c>、<c>制表符</c>、<c>换行</c>（含回车、分页、垂直制表），
        /// 只统计非空词（即去除空词后再计数）。
        /// </summary>
        /// <param name="text">要统计的文本。</param>
        /// <returns>词数；为空或 null 时返回 0。</returns>
        public static int CountWords(string text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return 0;
            }

            string[] words = text.Split(new[] { ' ', '\t', '\n', '\r', '\f', '\v' }, StringSplitOptions.RemoveEmptyEntries);
            return words.Length;
        }
    }
}
