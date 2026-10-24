namespace DouyinLive
{
    // 跳完一支舞的谢幕词。以前舞蹈一停就沉默，直播间经常冷场几秒；
    // 这里给一句带舞名的收尾，顺手提醒观众可以继续点舞/点赞。
    public static class DanceClosingLines
    {
        static readonly string[] Lines =
        {
            "谢谢大家看我跳《{0}》，喜欢的话点点小红心呀~",
            "《{0}》跳完啦，帅到你们的话扣个1让我看看！",
            "这支《{0}》还满意吗？想再看什么舞，弹幕点起来~",
            "呼~ 《{0}》有点累呢，但是你们开心就值得啦！",
        };

        public static string Pick(string title, int randomValue)
        {
            string name = string.IsNullOrWhiteSpace(title) ? "这支舞" : title.Trim();
            int i = randomValue % Lines.Length;
            if (i < 0) i += Lines.Length;
            return string.Format(Lines[i], name);
        }
    }
}
