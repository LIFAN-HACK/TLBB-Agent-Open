namespace TlbbAgent.Server.SharedKernel.Modules;

public sealed record ModuleDescriptor
{
  public ModuleDescriptor(string name, string description)
  {
    if (string.IsNullOrWhiteSpace(name))
    {
      throw new ArgumentException("Module name is required.", nameof(name));
    }

    if (string.IsNullOrWhiteSpace(description))
    {
      throw new ArgumentException("Module description is required.", nameof(description));
    }

    Name = name;
    Description = description;
  }

  public string Name { get; }

  public string Description { get; }
}
