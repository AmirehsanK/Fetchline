using System.Globalization;
using Fetchline.Core.Pipeline;
using Fetchline.Viz.Datapath;
using Fetchline.Viz.Export;

namespace Fetchline.Viz.Explain;

/// <summary>
/// The visualizer in Persian. The words are the ones Persian textbooks on computer architecture
/// use: «خط لوله» for the pipeline, «مخاطره» for a hazard, «انشعاب» for a branch, «ثبات» for a
/// register, «واکشی» for fetch. Forwarding is «پیش‌فرست» and a stall is «توقف».
///
/// What stays as it is: stage names, mnemonics, register names, the names of latches, and the
/// words a command line takes (<c>forwarding</c>, <c>not-taken</c>), which are the positions of
/// the switches on the page as well. Numbers are written with the same digits as the diagrams
/// and the hex dumps beside them, so that a 5 in a sentence is the 5 in the diagram.
///
/// A sentence here is read right to left with left-to-right names in it. It avoids brackets
/// round those names, which a mixed line tends to turn the wrong way: «add در ID» says what
/// <c>add (ID)</c> says.
/// </summary>
public sealed class PersianMessages : IMessages
{
    public static PersianMessages Instance { get; } = new();

    public string Stall => "توقف";

    public string Forward => "پیش‌فرست";

    public string Flush => "تخلیه";

    public string Trap => "تله";

    public string Wrong => "نادرست";

    public string NothingHappened => "هنوز توقف، پیش‌فرست یا تخلیه‌ای رخ نداده است.";

    public string LoadUse(string consumer, string register, string producer) =>
        $"بار و مصرف: {consumer} در ID به {register} نیاز دارد؛ {producer} در EX آن را تازه پس از MEM دارد";

    public string DataHazard(string consumer, string register, string producer, string producerStage) =>
        $"بدون پیش‌فرست: {consumer} در ID منتظر {register} می‌ماند تا {producer} از {producerStage} به WB برسد";

    public string BranchOperand(string branch, string register, string producer, string producerStage, string readyAfter) =>
        $"انشعاب در ID: {branch} در ID به {register} نیاز دارد؛ {producer} در {producerStage} آن را تازه پس از {readyAfter} دارد";

    public string MultiCycle(string instruction, string stage, int remaining) =>
        $"چندچرخه‌ای: {instruction} در {stage} هنوز {Number(remaining)} چرخه‌ی دیگر لازم دارد؛ هرچه پشت آن است منتظر می‌ماند";

    public string CacheMiss(CacheKind kind, string instruction, string stage, string block, int cycles, string? replaced) =>
        $"فقدان در {NameOf(kind)}: {instruction} در {stage} {Number(cycles)} چرخه منتظر بلوک {block} می‌ماند"
        + (kind == CacheKind.Data ? "؛ هرچه پشت آن است نیز منتظر می‌ماند" : string.Empty)
        + (replaced is null ? string.Empty : $"؛ بلوک {replaced} بیرون گذاشته می‌شود");

    public string NameOf(CacheKind kind) => kind == CacheKind.Instruction ? "حافظه‌ی نهان دستور" : "حافظه‌ی نهان داده";

    public string CacheSummary(string cache, int accesses, int misses) => string.Create(
        CultureInfo.InvariantCulture,
        $"{cache}: {accesses} دسترسی، {misses} فقدان، {(accesses == 0 ? 0 : 100.0 * misses / accesses):0.0} درصد");

    public string CacheHeading(CacheKind kind) => kind == CacheKind.Instruction ? "نهان دستور" : "نهان داده";

    public string CacheTitle => "حافظه‌ی نهان";

    // The cache is drawn as text in columns, like the comparison, so its headings are kept to
    // characters one cell wide: the terms a datasheet uses.
    public string SetHeading => EnglishMessages.Instance.SetHeading;

    public string WayHeading(int way) => EnglishMessages.Instance.WayHeading(way);

    public string CacheLegend => "هر راه بلوکی را نگه می‌دارد که از نشانیِ نوشته‌شده آغاز می‌شود. روشن: در این چرخه به کار رفت، و با ! اگر باید آورده می‌شد. کم‌رنگ: نخستین بلوکی که بیرون گذاشته می‌شود.";

    public string EachWith(string caches) => $"هر سطر با {caches}";

    public string CacheMissesHeading(CacheKind kind) => "فقدان‌های " + NameOf(kind);

    // The latch, the stage and the operand are one left-to-right run, as in the terminal.
    public string Forwarded(string latch, string stage, string operand, string register, string producer) =>
        $"{latch} -> {stage}.{operand}   {register} از {producer}";

    public string TakenBranch(string branch, string stage, string target, int squashed) =>
        $"{branch} در {stage} گرفته می‌شود و به {target} می‌رود؛ {Number(squashed)} دستور پشت آن دور ریخته می‌شود";

    public string NotTakenBranch(string branch, string stage, int squashed) =>
        $"{branch} در {stage} گرفته نمی‌شود، ولی پیش‌بینی شده بود که گرفته شود؛ {Number(squashed)} دستور پشت آن دور ریخته می‌شود";

    public string SystemFlush(string instruction, int squashed) =>
        $"{instruction} در MEM یک دستور سیستمی است؛ {Number(squashed)} دستور پشت آن دوباره واکشی می‌شود";

    public string Trapped(string instruction, string cause, string handler, int squashed) =>
        $"{instruction} در MEM به تله می‌افتد ({cause})؛ کنترل به {handler} می‌رود و {Number(squashed)} دستور دور ریخته می‌شود";

    public string Stopped(string instruction, int squashed) =>
        $"{instruction} در MEM ماشین را متوقف می‌کند؛ {Number(squashed)} دستور پشت آن دور ریخته می‌شود";

    public string Summary(ulong instructions, ulong cycles, double cpi, int stalls, int forwards, int flushes)
    {
        var text = string.Create(
            CultureInfo.InvariantCulture,
            $"{instructions} دستور، {cycles} چرخه، CPI {cpi:0.00}، {stalls} توقف، {forwards} پیش‌فرست");
        return flushes == 0 ? text : text + "، " + Number(flushes) + " تخلیه";
    }

    public string SquashedLegend(string mark) => $"{mark} دور ریخته شد: واکشی شد و سپس کنار گذاشته شد";

    public string HeldLegend => "یک چرخه‌ی دیگر نگه داشته شد";

    public string ThrownAwayLegend => "واکشی شد و سپس دور ریخته شد";

    public string ForwardedLegend => "مقداری که دست‌به‌دست شد";

    public string SourceTitle => "برنامه";

    public string PipelineTitle => "خط لوله";

    public string LogTitle => "رویدادها";

    public string RegistersTitle => "ثبات‌ها";

    public string MemoryTitle => "حافظه";

    public string ConsoleTitle => "کنسول";

    public string CountersTitle => "شمارنده‌ها";

    public string ExamplesMenu => "نمونه‌ها";

    public string NothingPrinted => "برنامه هنوز چیزی چاپ نکرده است.";

    public string NoMemory => "برنامه‌ای نیست، پس حافظه‌ای هم برای نمایش نیست.";

    // What the assembler says is in English, like the source it is about.
    public string Problem(string message, string? hint) => hint is null ? message : $"{message}; {hint}";

    public string MoreProblems(int count) => $"و {Number(count)} مورد دیگر";

    public string RunKey => "اجرا";

    public string PauseKey => "مکث";

    public string StepKey => "گام";

    public string BackKey => "عقب";

    public string ResetKey => "بازنشانی";

    public string SpeedKey(string pace) => pace.Length == 0 ? "سرعت بیشینه" : $"سرعت {Ltr(pace)}";

    public string CompareKey => "مقایسه";

    public string DiagramKey => "نمودار";

    public string CompareTitle => "مقایسه";

    public string Comparing(int done, int total) => $"اجرا به همه‌ی شکل‌ها: {Number(done)} از {Number(total)} انجام شد.";

    public string CompareHint => "سطری را برگزینید تا خط لوله همان‌گونه ساخته شود.";

    public string DatapathKey => "مسیر داده";

    public string StaircaseKey => "نمودار";

    public string DatapathTitle => "مسیر داده";

    public string NameOf(Signal signal) => signal switch
    {
        Signal.Pc => "pc",
        Signal.PcPlus4 => "pc + 4",
        Signal.NextPc => "pc بعدی",
        Signal.Predicted => "pc بعدی به پیش‌بینی",
        Signal.Target => "تغییر مسیر به",
        Signal.Instruction => "دستور",
        Signal.Rs1 => "rs1",
        Signal.Rs2 => "rs2",
        Signal.Rs1Value => "مقدار rs1",
        Signal.Rs2Value => "مقدار rs2",
        Signal.Imm => "مقدار فوری",
        Signal.AluA => "عملوند a در ALU",
        Signal.AluB => "عملوند b در ALU",
        Signal.Result => "نتیجه",
        Signal.Forwarded => "مقدار پیش‌فرستاده",
        Signal.Address => "نشانی",
        Signal.WriteData => "داده‌ی نوشتنی",
        Signal.ReadData => "داده‌ی خوانده‌شده",
        Signal.RdValue => "مقداری که در rd نوشته می‌شود",
        Signal.Hold => "نگه‌دار",
        _ => "گزینش مقدار پیش‌فرستاده",
    };

    public string WireCarries(string signal, string value) => $"{signal} = {value}";

    public string WireAsserted(string signal) => signal;

    public string WireIdle(string signal) => $"{signal}: در این چرخه به کار نرفت";

    public string DatapathHint => "سیم‌های روشن آن‌هایی‌اند که در این چرخه به کار رفته‌اند. برای دیدن مقدار، نشانگر را روی یکی ببرید.";

    public string ExportMenu => "برون‌بری";

    public string NameOf(ExportKind kind) => kind switch
    {
        ExportKind.Json => "ردگیری به‌صورت JSON",
        ExportKind.Kanata => "ردگیری به‌صورت Kanata",
        ExportKind.Svg => "نمودار به‌صورت SVG",
        _ => "نمودار به‌صورت PNG",
    };

    public string ShareKey => "هم‌رسانی";

    public string LinkCopied => "پیوند رونوشت شد: برنامه، کلیدها و همین چرخه";

    public string LinkInAddress => "پیوند در نوار نشانی است";

    public string LinkNotMade => "بیش از آن است که در یک پیوند جا شود";

    public string LinkNotRead(LinkProblem problem) => "پیوند خوانده نشد: " + problem switch
    {
        LinkProblem.Empty => "چیزی در آن نیست",
        LinkProblem.TooLong => "بیش از اندازه بلند است",
        LinkProblem.UnknownKind => "از گونه‌ای است که این صفحه نمی‌خواند",
        LinkProblem.NotText => "آنچه در آن است متن نیست",
        LinkProblem.UnknownSettings => "تنظیم‌هایی دارد که وجود ندارند",
        LinkProblem.CutShort => "در راه کوتاه شده یا تغییر کرده است",
        _ => "آسیب دیده است",
    };

    public string Timeline => "چرخه‌ی روی صفحه";

    public string TimelineRun(ulong cycles) => $"{Number(cycles)} چرخه اجرا شد";

    public string Controls => "کنترل‌های اجرا";

    public string StatusCycle(ulong cycle) => $"چرخه‌ی {Number(cycle)}";

    public string StatusReady => "آماده";

    public string StatusRunning => "در حال اجرا";

    public string StatusEnded => "پایان یافت";

    public string StatusExited(int code) => $"خروج با کد {Number(code)}";

    public string StatusPaused => "مکث در ebreak";

    public string StatusStopped(string reason) => $"متوقف شد: {reason}";

    public string StatusNothingToRun => "چیزی برای اجرا نیست";

    public string StatusWrong(ulong cycle) => $"نادرست از چرخه‌ی {Number(cycle)}";

    public string ReadyToRun => "RV32IM آماده است\n\n«گام» یک چرخه را اجرا می‌کند. «اجرا» تا پایان پیش می‌رود.";

    public string NothingToRun => "برنامه باید اسمبل شود تا بتواند اجرا شود.\nایرادهایش زیر برنامه فهرست شده است.";

    public string MoreCycles(ulong shownFrom, ulong shownTo, ulong total) =>
        $"چرخه‌های {Number(shownFrom)} تا {Number(shownTo)} از {Number(total)}؛ بقیه را {Ltr("--from")} و {Ltr("--cycles")} نشان می‌دهند";

    public string NoRegister => "هیچ ثباتی";

    public string NoStore => "هیچ";

    public string EndOfProgram => "پایان برنامه";

    public string FirstWrongValue(ulong index, ulong cycle, string instruction, string detail) =>
        $"نخستین مقدار نادرست: دستور {Number(index)}، {instruction}، در چرخه‌ی {Number(cycle)}: {detail}";

    public string WrongRegister(string pipeline, string reference) =>
        $"خط لوله {pipeline} را نوشت و ماشین مرجع {reference} را";

    public string WrongStore(string pipeline, string reference) =>
        $"خط لوله {pipeline} را ذخیره کرد و ماشین مرجع {reference} را";

    public string WrongPath(string pipeline, string reference) =>
        $"خط لوله به {pipeline} رفت و ماشین مرجع به {reference}";

    public string WrongInstruction(string pipeline, string reference) =>
        $"خط لوله {pipeline} را اجرا کرد، جایی که ماشین مرجع {reference} را اجرا کرد";

    // Persian letters join and are not each one cell wide, so the table keeps the terms of the
    // command line, which are also the ones a reader will type to get it.
    public string ColumnHeading(TableColumn column) => EnglishMessages.Instance.ColumnHeading(column);

    public string HazardsHeading => "مخاطره‌ها";

    public string BranchHeading => "انشعاب";

    public string PredictorHeading => "پیش‌بین";

    public string BufferHeading => "btb";

    public string MultiplyHeading => "ضرب و تقسیم";

    public string Switches => "شیوه‌ی ساخت خط لوله";

    public string CyclesHeading => "چرخه‌ها";

    public string CpiHeading => "CPI";

    public string StallsHeading => "توقف‌ها";

    public string SquashedHeading => "دورریخته‌ها";

    public string WrongGuessesHeading => "پیش‌بینی‌های نادرست";

    public string Guesses(int wrong, int decided) => $"{Number(wrong)}/{Number(decided)}";

    public string InstructionsHeading => "دستورها";

    public string FlushesHeading => "تخلیه‌ها";

    public string ForwardsHeading => "پیش‌فرست‌ها";

    public string BranchesHeading => "انشعاب‌ها";

    public string TakenHeading => "گرفته‌شده";

    public string LoadsHeading => "بارگذاری‌ها";

    public string StoresHeading => "ذخیره‌ها";

    public string TrapsHeading => "تله‌ها";

    public string NameOf(StallCause cause) => cause switch
    {
        StallCause.LoadUse => "بار و مصرف",
        StallCause.DataHazard => "بدون پیش‌فرست",
        StallCause.BranchOperand => "انشعاب در ID",
        StallCause.MultiCycle => "چندچرخه‌ای",
        StallCause.InstructionCacheMiss => "فقدان در حافظه‌ی نهان دستور",
        _ => "فقدان در حافظه‌ی نهان داده",
    };

    public string NameOf(FlushCause cause) => cause switch
    {
        FlushCause.Branch => "انشعاب",
        FlushCause.System => "دستور سیستمی",
        FlushCause.Trap => "تله",
        _ => "توقف ماشین",
    };

    public string WrongAnswer => "نادرست";

    public string CutOff(ulong cycles) => $"پس از {Number(cycles)} چرخه هنوز در حال اجراست";

    public string FewestCycles(ulong instructions, string configuration, ulong cycles) =>
        $"{Number(instructions)} دستور؛ کمترین چرخه با پاسخ درست: {Ltr(configuration)}، {Number(cycles)} چرخه";

    public string WrongWith(string configuration, string firstWrongValue) => $"{Ltr(configuration)}: {firstWrongValue}";

    /// <summary>
    /// Something written from left to right that has signs in it (a pace, a row of switches),
    /// kept in one piece: without the two marks round it, a line that runs the other way would
    /// put its parts in the wrong order.
    /// </summary>
    private static string Ltr(string text) => (char)0x2066 + text + (char)0x2069;

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(ulong value) => value.ToString(CultureInfo.InvariantCulture);
}
