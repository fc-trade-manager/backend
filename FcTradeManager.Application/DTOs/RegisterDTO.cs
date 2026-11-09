using System.ComponentModel.DataAnnotations;

namespace FcTradeManager.Application.DTOs;

public class RegisterDto
{
    [Required]
    [EmailAddress]
    public string Email { get; set; } = string.Empty;

    [Required]
    [MinLength(8, ErrorMessage = "The password must be at least 8 characters long.")]
    public string Password { get; set; } = string.Empty;

    [Required]
    [RegularExpression("^[A-Za-z0-9_]{3,18}$",
    ErrorMessage = "Username must be 3-20 characters: letters, numbers or underscore.")]
    public string Username { get; set; } = string.Empty;

    [Required]
    [StringLength(25, MinimumLength = 3)]
    public string DisplayName { get; set; } = string.Empty;
}