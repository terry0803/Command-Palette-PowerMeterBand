using Microsoft.CommandPalette.Extensions;
using Microsoft.CommandPalette.Extensions.Toolkit;
using Windows.Foundation.Collections;

namespace PowerMeterBand;

/// <summary>
/// ListItem that reserves a fixed Dock label width and uses tabular digits, matching the
/// built-in Performance Monitor bands. SDK 0.12 has no helpers for this yet, so the
/// well-known attribute keys are set directly.
/// </summary>
internal sealed partial class DockValueListItem : ListItem, IExtendedAttributesProvider
{
    private readonly ValueSet _attributes = new();

    public DockValueListItem(ICommand command, double titleWidthDips)
        : base(command)
    {
        _attributes["Microsoft.CommandPalette.Dock.TitleWidth"] = titleWidthDips;
        _attributes["Microsoft.CommandPalette.Dock.TabularDigits"] = true;
    }

    // Re-implements the interface so the host sees these hints; hides CommandItem's own bag.
    public new IDictionary<string, object> GetProperties() => _attributes;
}
