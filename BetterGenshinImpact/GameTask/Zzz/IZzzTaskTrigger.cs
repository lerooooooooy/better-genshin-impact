using BetterGenshinImpact.GameTask.Trigger;

namespace BetterGenshinImpact.GameTask.Zzz;

public interface IZzzTaskTrigger
{
    string Name { get; }
    bool IsEnabled { get; set; }
    void OnCapture(TriggerCaptureContent content);
}
