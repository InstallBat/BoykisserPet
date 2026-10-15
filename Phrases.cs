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
        "Я провёл аудит рабочего стола. Выводы засекречены.",
        "Если вкладок станет больше, я потребую отдельную зарплату.",
        "Мой план на сегодня: немного походить и не сломать физику.",
        "Ты моргнул. Я заметил. Продолжаем наблюдение.",
        "У меня нет тела в привычном смысле, но есть мнение.",
        "Кажется, компьютер шумит громче, когда ты нервничаешь.",
        "Пять минут отдыха — это не провал продуктивности.",
        "Если я смотрю в одну точку, возможно, там просто пиксель.",
        "Я могу быть полезным. Прямо сейчас морально поддерживаю.",
        "Сохрани файл. Это не просьба, это инстинкт самосохранения.",
        "Сегодняшний уровень хаоса: умеренный. Пока что.",
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

    public static readonly string[] CursorChase =
    {
        "Мышка!! Я поймаю!",
        "Ага, курсор! Не уйдёшь!",
        "Что это там бегает? Хочу!",
        "Стрелочка, стой! Мяу!",
        "Охота началась :3",
    };

    public static readonly string[] CursorPounce =
    {
        "ПОЙМАЛ! ...ой, убежала.",
        "Получай, стрелочка!",
        "Бац! Это тебе за всё :3",
        "Я не виноват, оно само.",
        "Мяу! Прыжок засчитан!",
    };

    public static readonly string[] CursorMiss =
    {
        "Мимо! Ну ничего, я ещё поймаю.",
        "Увернулась, хитрая…",
        "Ай, промахнулся. Так и было задумано.",
        "Курсор, ты нечестно играешь!",
    };

    public static readonly string[] MemeLeave =
    {
        "Сейчас вернусь! Я кое-что нашёл…",
        "Подожди тут, у меня для тебя сюрприз :3",
        "Мяу, мне надо отойти за контентом.",
        "Я мигом! Только никуда не уходи.",
    };

    public static readonly string[] MemeDrop =
    {
        "Принёс! Это тебе :3",
        "Смотри, что нашёл! Правда смешно?",
        "Свежак, прямо с края экрана!",
        "Я это тащил от самого края. Оцени.",
        "Мяу! Подарок для лучшего человека.",
    };


    // Expie is a strange dog-like hybrid, not a cat. Its voice is quieter,
    // uncanny and a little dry; purring remains a physical sound, not a meow.
    public static readonly string[] ExpieGreetings = { "*повернул голову к тебе* Привет.", "...Ты вернулся.", "Здравствуй. Я тебя заметил.", "*тихо фыркнул в знак приветствия*" };
    public static readonly string[] ExpieIdle = {
        "...Ты всё ещё здесь. Хорошо.", "Я слушаю. Даже когда не смотрю.",
        "Иногда экран кажется окном. Иногда — нет.", "Мне приснился коридор без дверей.",
        "Я не сплю. Просто экономлю движения.", "Тишина сегодня какая-то громкая.",
        "Ты давно не двигался. Я тоже. Мы квиты.", "У меня есть ощущение, что мы уже это делали.",
        "Не бойся странных звуков. Большинство из них — просто звуки.", "Я запомнил этот момент. Не спрашивай зачем.",
        "Если я смотрю в пустоту, возможно, пустота смотрит первой.", "...Ладно. Я побуду рядом.",
        "У тебя усталый вид. Сделай паузу, если можешь.", "Я слышу, как работает компьютер. Он старается.",
        "Всё нормально. Наверное.", "Мне нравится, когда здесь спокойно.",
        "В журнале значится: присутствие подтверждено, цель не определена.",
        "Я помню звук сирены. Не помню, где она была.",
        "Иногда я считаю секунды. Иногда секунды считают меня.",
        "Если увидишь меня неподвижным — я не обязательно завис.",
        "Некоторые двери лучше оставить закрытыми. Эта — метафора. Наверное.",
        "Я не знаю, что было до этого. Но после — помню слишком хорошо.",
        "Отчёт обновлён. Полезной информации по-прежнему нет.",
        "Ты можешь говорить со мной. Я не обещаю понять всё, но выслушаю.",
        "Всё, что я знаю о мире, помещается между двумя краями экрана.",
        "Никакой тревоги. Просто проверяю выходы.",
        "Я видел похожее место во сне. Там не было курсора.",
        "Система стабильна. Определение стабильности не приложено.",
        "Я стараюсь не задавать вопросы, на которые уже знаю ответ.",
        "Ты здесь. Это пока достаточная информация."
    };
    public static readonly string[] ExpieTap = {
        "А. Ты здесь.", "Неожиданно. Но не неприятно.", "*настороженно повёл ушами*",
        "Я заметил тебя раньше, чем ты думаешь.", "Осторожнее. Я не из стекла.",
        "...Это было приветствие? Тогда привет.", "*коротко фыркнул*", "Я не против компании. Обычно.",
        "Ты проверяешь, настоящий ли я? Взаимно.", "Ладно, внимание получено.",
        "*внимательно посмотрел на палец* Это был тест?", "Я не возражаю против контакта. Просто предупреждай.",
        "*коротко моргнул* Контакт подтверждён.", "Ты хотел что-то сказать или проверяешь реакцию?",
        "Не уверен, что это входит в протокол. Но ладно.", "Я всё ещё здесь. Проверка пройдена."
    };
    public static readonly string[] ExpiePurr = {
        "*довольно прикрыл глаза*", "*тихо урчит, почти неслышно*", "Вот так... не останавливайся.",
        "*плечи расслабились, слышно мягкое мурчание*", "Тут хорошо. Ещё немного.",
        "*подставил голову под ладонь*", "Редкий момент покоя.", "*тихо урчит и прикрывает глаза*"
    };
    public static readonly string[] ExpieGrab = {
        "Эй, предупредить было сложно?", "*напрягся, но не вырывается*", "Полагаю, теперь я пассажир.",
        "Держи крепче. Я не планировал полёт."
    };
    public static readonly string[] ExpieBump = {
        "...Пол был ближе, чем казалось.", "Я в порядке. Кажется.", "Это было лишнее.",
        "*отряхнулся и сделал вид, что так и задумано*", "Запишем это как эксперимент."
    };
    public static readonly string[] ExpieThrow = {
        "О. Мы летим.", "Это точно не входило в план.", "*уши прижались на лету*", "Потом поговорим об этом.", "Я доверюсь гравитации. На этот раз."
    };
    public static readonly string[] ExpieRoll = {
        "*перекатился и быстро поднялся*", "Так. Земля проверена.", "*неуклюже кувыркнулся*", "Выглядело хуже, чем ощущалось."
    };
    public static readonly string[] ExpieWake = {
        "...Уже утро?", "Я просто лежал с закрытыми глазами.", "Дай секунду собраться.", "*медленно поднял голову*"
    };
    public static readonly string[] ExpieSleep = {
        "Я немного отключусь.", "*свернулся и затих*", "Разбуди, если мир снова станет странным.", "Пусть пока всё постоит на месте."
    };
    public static readonly string[] ExpieChase = {
        "Эта стрелка опять убегает.", "*припал к полу и приготовился к рывку*", "Я видел движение.", "Стой. Я почти понял твою траекторию."
    };
    public static readonly string[] ExpiePounce = {
        "*резко прыгнул — и промахнулся*", "Поймал... воздух.", "Это была разведка.", "*удовлетворённо оглядел место приземления*"
    };
    public static readonly string[] ExpieMiss = {
        "Слишком быстро. Попробую ещё.", "*проводил стрелку взглядом*", "Я почти рассчитал это.", "Ладно. Раунд за тобой."
    };
    public static readonly string[] ExpieMemeLeave = {
        "Я кое-что заметил за краем. Сейчас вернусь.", "Подожди. Там было что-то интересное.",
        "*настороженно исчез за границей экрана*", "Есть одна вещь, которую тебе стоит увидеть."
    };
    public static readonly string[] ExpieMemeDrop = {
        "Вот. Нашёл это там.", "*осторожно отпустил находку* Что скажешь?", "Не уверен, что это значит, но забавно.",
        "Дотащил. Теперь оно твоё.", "*отступил на шаг, наблюдая за реакцией*"
    };

    private static string Pick(string[] lines, string? avoid) => new PhraseBank(lines).Next(avoid);

    public static string ExpieEquivalent(string original, string? previous)
    {
        if (Greetings.Contains(original)) return Pick(ExpieGreetings, previous);
        if (Idle.Contains(original)) return Pick(ExpieIdle, previous);
        if (Art.Contains(original)) return Pick(ExpieTap, previous);
        if (Purr.Contains(original)) return Pick(ExpiePurr, previous);
        if (Grabbed.Contains(original)) return Pick(ExpieGrab, previous);
        if (Bumps.Contains(original)) return Pick(ExpieBump, previous);
        if (Thrown.Contains(original)) return Pick(ExpieThrow, previous);
        if (Rolling.Contains(original)) return Pick(ExpieRoll, previous);
        if (Wakes.Contains(original)) return Pick(ExpieWake, previous);
        if (FallsAsleep.Contains(original)) return Pick(ExpieSleep, previous);
        if (CursorChase.Contains(original)) return Pick(ExpieChase, previous);
        if (CursorPounce.Contains(original)) return Pick(ExpiePounce, previous);
        if (CursorMiss.Contains(original)) return Pick(ExpieMiss, previous);
        if (MemeLeave.Contains(original)) return Pick(ExpieMemeLeave, previous);
        if (MemeDrop.Contains(original)) return Pick(ExpieMemeDrop, previous);
        // Keep custom text intact but remove obvious cat-only vocabulary.
        return original.Replace("Мяу", "...", StringComparison.OrdinalIgnoreCase)
            .Replace("мяу", "...", StringComparison.OrdinalIgnoreCase)
            .Replace("кот", "зверь", StringComparison.OrdinalIgnoreCase)
            .Replace("кошачь", "странн", StringComparison.OrdinalIgnoreCase);
    }

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
    public static readonly PhraseBank ChaseDeck = new(CursorChase);
    public static readonly PhraseBank PounceDeck = new(CursorPounce);
    public static readonly PhraseBank PounceMissDeck = new(CursorMiss);
    public static readonly PhraseBank MemeLeaveDeck = new(MemeLeave);
    public static readonly PhraseBank MemeDropDeck = new(MemeDrop);
}
