namespace BlazorWasmDotNet8AspNetCoreHosted.Shared.DTOs;

// DTO для керування кафедрами.
public record class DepartmentEditDto
{
    // Порожній конструктор для серіалізації/десеріалізації.
    public DepartmentEditDto() { }
    // Зручна ініціалізація DTO для форми редагування або створення.
    public DepartmentEditDto(int? id, string name, bool isActive = true)
    {
        Id = id;
        Name = name;
        IsActive = isActive;
    }
    // Ідентифікатор кафедри (null для створення).
    public int? Id { get; set; }
    // Назва кафедри.
    public string Name { get; set; } = "";
    // Ознака активності кафедри.
    public bool IsActive { get; set; } = true;
}
