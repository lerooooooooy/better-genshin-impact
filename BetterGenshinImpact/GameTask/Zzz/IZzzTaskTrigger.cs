namespace BetterGenshinImpact.GameTask.Zzz;

public interface IZzzTaskTrigger
{
    string Name { get; }
    bool IsEnabled { get; set; }
    void OnCapture(ZzzCaptureContent content);
}
