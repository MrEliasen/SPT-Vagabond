namespace Vagabond.Common.Definitions;

public class CustomExtractRequirementDefinition
{
    public CustomExfilRequirementType Type { get; set; } = CustomExfilRequirementType.None;
    public string Id { get; set; } = "";
    public int Count { get; set; } = 1;
    public string RequiredSlot { get; set; } = "";
    public string RequirementTip { get; set; } = "";
    public bool ApplyDiscount { get; set; } = false;
}