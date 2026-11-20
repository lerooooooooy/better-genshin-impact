using BetterGenshinImpact.GameTask.Trigger;

namespace BetterGenshinImpact.GameTask.WuWa;

public interface IWuWaTaskTrigger
{
    string Name { get; }
    bool IsEnabled { get; set; }
    void OnCapture(TriggerCaptureContent content);
}
