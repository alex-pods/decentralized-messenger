namespace MessengerDesktop;

/// <summary>
/// Юнит-проверка порядка закрепов без UI.
/// Запуск: dotnet run --project desktop/MessengerDesktop.csproj -- --check-pins
/// </summary>
public static class PinStripCheck
{
    public static int Run()
    {
        try
        {
            // Лента: индексы 0..9 сверху вниз. Закрепы на 2, 5, 8 → в полоске [8, 5, 2].
            int[] pins = [8, 5, 2];

            Expect(0, PinStrip.IndexForScroll(9, pins), "низ чата — самый нижний закреп");
            Expect(0, PinStrip.IndexForScroll(8, pins), "нижний закреп на нижнем краю — ещё он");
            Expect(1, PinStrip.IndexForScroll(7, pins), "пролистали нижний вверх — следующий выше");
            Expect(1, PinStrip.IndexForScroll(5, pins), "средний на нижнем краю — он");
            Expect(2, PinStrip.IndexForScroll(4, pins), "пролистали средний — верхний");
            Expect(2, PinStrip.IndexForScroll(2, pins), "верхний на краю — он");
            Expect(2, PinStrip.IndexForScroll(0, pins), "выше всех закрепов — верхний в полоске");
            Expect(0, PinStrip.IndexForScroll(3, [8]), "один закреп всегда 0");
            Expect(2, PinStrip.IndexForScroll(0, [-1, -1, -1]), "закрепы не в окне — последний в полоске");
            Expect(1, PinStrip.InsertIndex([30, 20, 10], 25), "вставка по id DESC");
            Expect(0, PinStrip.InsertIndex([30, 20, 10], 40), "новый самый нижний — в начало");
            Expect(3, PinStrip.InsertIndex([30, 20, 10], 5), "самый верхний — в конец");

            Console.WriteLine("OK pin-strip");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("FAIL " + ex.Message);
            return 1;
        }
    }

    static void Expect(int want, int got, string label)
    {
        if (want != got)
            throw new Exception($"{label}: ожидали {want}, получили {got}");
    }
}
