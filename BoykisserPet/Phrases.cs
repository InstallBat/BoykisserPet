namespace BoykisserPet;

/// <summary>
/// Speech lines, shuffled as a "deck" so every line is heard before any
/// repeats — the same anti-repetition trick used in the reference exe, which
/// re-shuffles once the bag empties and avoids repeating across the seam.
/// </summary>
public sealed class PhraseBank
{
    private readonly string[] _lines;
    private readonly List<int> _bag = new();

    public PhraseBank(string[] lines) => _lines = lines;

    public string Next(string? avoidRepeating = null)
    {
        if (_bag.Count == 0) Refill(avoidRepeating);
        int index = _bag[^1];
        _bag.RemoveAt(_bag.Count - 1);
        return _lines[index];
    }

    private void Refill(string? avoidRepeating)
    {
        for (int i = 0; i < _lines.Length; i++) _bag.Add(i);

        for (int i = _bag.Count - 1; i > 0; i--)
        {
            int j = Random.Shared.Next(i + 1);
            (_bag[i], _bag[j]) = (_bag[j], _bag[i]);
        }

        // The last drawn element is the next one out, so push a duplicate of
        // the previous line to the front to keep it from landing there.
        if (avoidRepeating is not null && _bag.Count > 1 && _lines[_bag[^1]] == avoidRepeating)
            (_bag[0], _bag[^1]) = (_bag[^1], _bag[0]);
    }
}

public static class Phrases
{
    public static readonly string[] Greetings =
    {
        "Привет! Я бойкиссер. Буду ходить тут и мяукать.",
        "Мяу! Я поселился у тебя на рабочем столе.",
        "Привет-привет! Тапни меня, если что.",
    };

    /// <summary>Idle chatter — the reference exe's spirit, rewritten for a cat.</summary>
    public static readonly string[] Idle =
    {
        "Мяу. Просто мяу.",
        "Я тут подумал: поспать бы.",
        "Не забудь попить воды, человек.",
        "Ты давно не разминал спину. Я слежу.",
        "Мур-мур-мур…",
        "Я самый красивый кот на этом мониторе.",
        "Разбудишь — поцарапаю. Шучу. Наверное.",
        "Хочешь погладить? Гладить надо сверху, я разрешаю.",
        "Если я мешаю — просто перетащи меня в угол.",
        "Говорят, я отвлекаю. Неправда. Мяу.",
        "Ctrl+S. Просто напоминаю.",
        "Я видел, как ты открыл двадцать вкладок. Я всё видел.",
        "Сделай перерыв, я посторожу экран.",
        "Идеально — это когда поспал восемь часов. Не я, а ты.",
        "Я не толстый, я пушистый.",
        "Мяу? Мяу. Мяу!",
        "Что-то тут скучно. Может, погоняем?",
        "Не трогай меня, я занят. Я ничего не делаю.",
        "Я умею ходить по экрану. Это мой талант.",
        "Посиди со мной минуту. Ничего не делай. Вот так.",
        "У тебя есть еда? У меня нет. Это несправедливо.",
        "Я пробовал быть серьёзным. Не получилось.",
        "Кто хороший мальчик? Это ты. Ну и я немножко.",
        "Проветри комнату, человек.",
        "Я сплю не потому, что лень. Я сплю профессионально.",
        "Если долго смотреть на меня, я начну мурчать.",
        "Мне бы коробку. Любую коробку.",
        "Ты молодец. Просто напоминаю.",
        "Мяу. Это значит «привет» на кошачьем.",
        "Погладь меня. Или хотя бы тапни.",
    };

    /// <summary>Reactions to a click.</summary>
    public static readonly string[] Art = new[]
    {
        "Мяу!",
        "Мур!",
        "Ай! Аккуратнее с ушами.",
        "Ты меня тапнул. Я запомнил.",
        "Ещё раз — и я мяукну громче.",
        "Мяу-у! Я тут!",
        "Хи-хи, щекотно.",
        "Ой. Ты чего?",
        "Я не кнопка, я кот.",
        "Мур-мяу! Привет.",
        "Ты меня разбудил. Ладно, я не спал.",
        "Мяу. Доволен?",
        "Гладить надо аккуратно, а не тыкать.",
        "Ещё! Ещё разок.",
        "Мяу-мяу-мяу. Всё, хватит.",
        "Ты меня нашёл. Молодец.",
        "Пф. Бесцеремонно.",
        "Муррр… ладно, продолжай.",
        "Я весь внимание. И весь мех.",
        "Ты клавиатуру не трогай, а меня трогай.",
    };

    /// <summary>Petting (top-half stroke).</summary>
    public static readonly string[] Purr =
    {
        "Мурррр…",
        "Мур-мур-мур…",
        "Ещё немножко…",
        "Вот тут, за ушком.",
        "Мурр. Ты умеешь.",
        "Муррр! Лучший человек.",
        "Так, хорошо… не останавливайся.",
        "Мур-мур. Я таю.",
    };

    /// <summary>When startled by being scooped up.</summary>
    public static readonly string[] Grabbed =
    {
        "Ой! Куда мы?",
        "Мяу?!",
        "Аккуратно, я кот, а не мяч!",
        "Неси, неси. Мне тоже интересно.",
    };

    /// <summary>After a hard fling or landing.</summary>
    public static readonly string[] Bumps =
    {
        "Бум! Мяу.",
        "Я в порядке! Мех цел.",
        "Стена. Внезапно.",
        "Это было по-кошачьи грациозно. Почти.",
        "Ещё раз! Ещё!",
        "Ох. Голова кружится.",
    };

    public static readonly string[] Thrown =
    {
        "Я лечу-у-у!",
        "Мяяяу-у-у!",
        "Я не птица!",
        "Уи-и-и!",
        "Кошки не летают. Но я попробовал.",
    };

    public static readonly string[] Rolling =
    {
        "Покатился-а-а!",
        "Я колесо! Я свобода!",
        "Мяу-боулинг!",
        "Разойдись, я качусь!",
    };

    public static readonly string[] Wakes =
    {
        "Ммм… кто тут?",
        "Мяу… я спал.",
        "Пять минут ещё…",
        "Я не спал. Я думал.",
    };

    public static readonly string[] FallsAsleep =
    {
        "Ммм… сплю…",
        "Ушёл в кошачий сон…",
        "Тише. Я отдыхаю.",
    };

    // Decks — one per behaviour, so lines don't bleed across contexts.
    public static readonly PhraseBank IdleDeck = new(Idle);
    public static readonly PhraseBank TapDeck = new(Art);
    public static readonly PhraseBank PurrDeck = new(Purr);
    public static readonly PhraseBank GrabDeck = new(Grabbed);
    public static readonly PhraseBank BumpDeck = new(Bumps);
    public static readonly PhraseBank ThrowDeck = new(Thrown);
    public static readonly PhraseBank RollDeck = new(Rolling);
    public static readonly PhraseBank WakeDeck = new(Wakes);
    public static readonly PhraseBank SleepDeck = new(FallsAsleep);
    public static readonly PhraseBank GreetDeck = new(Greetings);
}
