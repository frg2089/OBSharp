using System.ComponentModel.DataAnnotations;

namespace OBSharp.Runtime;

internal sealed class OBSharpHostOptions
{
    [Required]
    public RuntimeFramework RuntimeFramework { get; set; }
    [Required]
    public string ContentPath { get; set; } = default!;
}
