namespace MessengerDesktop;

/// <summary>
/// Порядок закрепов как в Telegram: сначала самое нижнее в чате (больший sequence).
/// При прокрутке вверх закреп уходит за нижний край кадра — тогда в полоске следующий выше.
/// </summary>
public static class PinStrip
{
    /// <param name="lastVisibleMsgIndex">Индекс самого нижнего видимого сообщения в ленте.</param>
    /// <param name="pinMsgIndices">Индексы закрепов в ленте, уже в порядке полоски (снизу вверх по чату).</param>
    public static int IndexForScroll(int lastVisibleMsgIndex, IReadOnlyList<int> pinMsgIndices)
    {
        if (pinMsgIndices.Count == 0) return 0;
        if (pinMsgIndices.Count == 1) return 0;

        // Самый нижний закреп, который ещё не ушёл полностью под нижний край кадра.
        for (var i = 0; i < pinMsgIndices.Count; i++)
        {
            var msgAt = pinMsgIndices[i];
            if (msgAt < 0) continue;
            if (msgAt <= lastVisibleMsgIndex)
                return i;
        }

        // Все закрепы ниже кадра (мы выше всех) — самый верхний в полоске.
        return pinMsgIndices.Count - 1;
    }

    public static int InsertIndex(IReadOnlyList<long> sequencesDesc, long sequence)
    {
        for (var i = 0; i < sequencesDesc.Count; i++)
        {
            if (sequencesDesc[i] < sequence) return i;
        }
        return sequencesDesc.Count;
    }
}
