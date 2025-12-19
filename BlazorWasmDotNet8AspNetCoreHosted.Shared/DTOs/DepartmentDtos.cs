namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

/// <summary>
/// DTO для керування кафедрами.
/// </summary>
public record class DepartmentEditDto
{
    public DepartmentEditDto() { }

    public DepartmentEditDto(int? id, string name, bool isActive = true)
    {
        Id = id;
        Name = name;
        IsActive = isActive;
    }

    public int? Id { get; set; }
    public string Name { get; set; } = "";
    public bool IsActive { get; set; } = true;
}

