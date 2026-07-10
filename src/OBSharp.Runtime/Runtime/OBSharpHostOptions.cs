using System.ComponentModel.DataAnnotations;

namespace OBSharp.Runtime;

internal sealed class OBSharpHostOptions
{
    public bool UseAspNet { get; set; }
    public bool UseWindowsDesktop { get; set; }
    [Required]
    public string ContentPath { get; set; } = default!;
}
