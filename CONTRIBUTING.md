# Contributing to Sendspin Windows Client

Thank you for your interest in contributing to the Sendspin Windows Client! This document provides guidelines and information to help you get started.

## Table of Contents
- [Code of Conduct](#code-of-conduct)
- [Getting Started](#getting-started)
- [Development Setup](#development-setup)
- [Project Structure](#project-structure)
- [Code Style Guidelines](#code-style-guidelines)
- [Testing](#testing)
- [Pull Request Process](#pull-request-process)
- [Code Review Expectations](#code-review-expectations)
- [Commit Messages](#commit-messages)
- [Documentation](#documentation)

## Code of Conduct

This project adheres to a code of conduct that we expect all contributors to follow:
- Be respectful and inclusive
- Welcome newcomers and help them learn
- Focus on constructive feedback
- Assume good intentions

## Getting Started

1. **Fork the repository** on GitHub
2. **Clone your fork** locally:
   ```bash
   git clone https://github.com/your-username/windowsSpin.git
   cd windowsSpin
   ```
3. **Add the upstream repository**:
   ```bash
   git remote add upstream https://github.com/original-owner/windowsSpin.git
   ```
4. **Create a branch** for your changes:
   ```bash
   git checkout -b feature/your-feature-name
   ```

## Development Setup

### Required Tools

- **Visual Studio 2022** (17.8 or later) with:
  - .NET desktop development workload
  - Windows 10 SDK (10.0.17763.0)
- **OR JetBrains Rider** (2023.3 or later)
- **.NET 10.0 SDK**
- **Git** for version control

### Recommended Extensions (Visual Studio)

- **ReSharper** or **Visual Studio IntelliCode**
- **CodeMaid** - Code cleanup and organization
- **Markdown Editor** - For documentation editing

### Initial Setup

1. **Restore NuGet packages**:
   ```bash
   dotnet restore
   ```

2. **Build the solution**:
   ```bash
   dotnet build
   ```

3. **Verify the build succeeds** with no errors

4. **Run the application**:
   ```bash
   dotnet run --project src/SendspinClient/SendspinClient.csproj
   ```

### Setting Up a Music Assistant Server

For development and testing, you need a Music Assistant server with Sendspin support:

1. **Install Music Assistant** following the [official documentation](https://music-assistant.io/installation/)
2. **Enable Sendspin** in Music Assistant settings
3. **Note the server IP/hostname** for testing

## Project Structure

```
windowsSpin/
├── src/
│   ├── SendspinClient.Core/          # Core protocol implementation
│   │   ├── Client/                    # Client services and capabilities
│   │   ├── Connection/                # WebSocket connection management
│   │   ├── Discovery/                 # mDNS discovery and advertisement
│   │   ├── Protocol/                  # Message serialization and parsing
│   │   │   └── Messages/              # Protocol message types
│   │   ├── Models/                    # Data models
│   │   └── Synchronization/           # Clock synchronization
│   ├── SendspinClient.Services/       # Windows audio services
│   └── SendspinClient/                # WPF application
│       ├── ViewModels/                # MVVM view models
│       ├── Resources/                 # UI resources and converters
│       └── MainWindow.xaml            # Main UI
├── tests/                             # Unit and integration tests (planned)
├── .editorconfig                      # Editor configuration
├── stylecop.json                      # StyleCop settings
├── CodeAnalysis.ruleset               # Analyzer configuration
├── Directory.Build.props              # Shared MSBuild properties
└── SendspinClient.sln                 # Solution file
```

## Code Style Guidelines

This project enforces consistent code style through automated analyzers and `.editorconfig` settings.

### Code Analyzers

The project uses three code analyzers configured in `Directory.Build.props`:

1. **Roslynator** - Comprehensive code analysis and refactoring
2. **StyleCop** - Code style enforcement
3. **SonarAnalyzer** - Code quality checks

### General Guidelines

#### Formatting
- **Indentation**: 4 spaces (no tabs)
- **Line endings**: CRLF (Windows)
- **Encoding**: UTF-8 with BOM
- **End of file**: Blank line required

#### Naming Conventions
- **Private fields**: Camel case with underscore prefix (`_fieldName`)
- **Public members**: Pascal case (`PropertyName`, `MethodName()`)
- **Constants**: Pascal case (`MaxBufferSize`)
- **Interfaces**: Pascal case with `I` prefix (`IConnection`)
- **Type parameters**: Pascal case with `T` prefix (`TMessage`)

#### Code Organization
- **Using directives**: Outside namespace, system directives first
- **File-scoped namespaces**: Use file-scoped namespace declarations
- **One type per file**: Each public type in its own file
- **File naming**: Match the primary type name

### C# Style Preferences

#### Variables
```csharp
// Avoid var for built-in types
int count = 10;
string name = "test";

// Use var when type is apparent
var client = new SendspinClientService();
var servers = GetDiscoveredServers();
```

#### Expression-bodied Members
```csharp
// Use for simple properties
public string Name => _name;

// Use for single-line methods
public int GetCount() => _items.Count;

// Avoid for constructors
public MyClass(string name)
{
    _name = name;
}
```

#### Pattern Matching
```csharp
// Prefer pattern matching
if (obj is string text)
{
    Process(text);
}

// Use switch expressions
var result = value switch
{
    0 => "zero",
    > 0 => "positive",
    < 0 => "negative"
};
```

#### Null Checking
```csharp
// Use nullable reference types
public void Process(string? input)
{
    if (input is null) return;
    // Process non-null input
}

// Use null-coalescing
var value = input ?? defaultValue;

// Use null-conditional
var length = text?.Length ?? 0;
```

### Documentation Comments

All public APIs must have XML documentation comments:

```csharp
/// <summary>
/// Connects to a Sendspin server asynchronously.
/// </summary>
/// <param name="serverUri">The WebSocket URI of the server.</param>
/// <param name="cancellationToken">Cancellation token to cancel the operation.</param>
/// <returns>A task representing the asynchronous connection operation.</returns>
/// <exception cref="ArgumentNullException">Thrown when <paramref name="serverUri"/> is null.</exception>
/// <exception cref="TimeoutException">Thrown when the handshake times out.</exception>
public async Task ConnectAsync(Uri serverUri, CancellationToken cancellationToken = default)
{
    // Implementation
}
```

**Required Tags:**
- `<summary>`: Brief description of the member
- `<param>`: Description for each parameter
- `<returns>`: Description of return value (for non-void methods)
- `<exception>`: Document exceptions that can be thrown

### Asynchronous Code

```csharp
// Always suffix async methods with Async
public async Task ConnectAsync()

// Always accept CancellationToken
public async Task ProcessAsync(CancellationToken cancellationToken = default)

// Use ConfigureAwait(false) in libraries
await SomeOperationAsync().ConfigureAwait(false);

// Avoid async void (except event handlers)
private async void OnButtonClick(object sender, EventArgs e)
```

### Error Handling

```csharp
// Use specific exceptions
throw new ArgumentNullException(nameof(parameter));
throw new InvalidOperationException("Connection not established");

// Log exceptions before re-throwing
catch (Exception ex)
{
    _logger.LogError(ex, "Failed to process message");
    throw;
}

// Use when clause for specific handling
catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested)
{
    _logger.LogError("Operation timed out");
}
```

### Dependency Injection

```csharp
// Constructor injection for required dependencies
public class MyService
{
    private readonly ILogger<MyService> _logger;
    private readonly IConnection _connection;

    public MyService(
        ILogger<MyService> logger,
        IConnection connection)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _connection = connection ?? throw new ArgumentNullException(nameof(connection));
    }
}

// Optional parameters for optional dependencies
public MyService(
    ILogger<MyService> logger,
    IConnection connection,
    IClockSynchronizer? clockSync = null)
{
    _clockSync = clockSync ?? new DefaultClockSynchronizer();
}
```

## Testing

### Test Structure (Planned)

Tests will be organized into:
- **Unit Tests**: Test individual components in isolation
- **Integration Tests**: Test component interactions
- **End-to-End Tests**: Test complete workflows

### Writing Tests

```csharp
[Fact]
public async Task ConnectAsync_WithValidUri_ShouldConnect()
{
    // Arrange
    var client = CreateClient();
    var serverUri = new Uri("ws://localhost:8080/sendspin");

    // Act
    await client.ConnectAsync(serverUri);

    // Assert
    Assert.Equal(ConnectionState.Connected, client.ConnectionState);
}
```

### Running Tests

```bash
# Run all tests
dotnet test

# Run tests with coverage
dotnet test /p:CollectCoverage=true
```

## Pull Request Process

### Before Submitting

1. **Ensure code builds** without errors or warnings:
   ```bash
   dotnet build --configuration Release
   ```

2. **Fix all analyzer warnings**:
   - StyleCop warnings
   - Roslynator suggestions
   - SonarAnalyzer issues

3. **Add/update documentation**:
   - XML documentation for public APIs
   - README.md for new features
   - Code comments for complex logic

4. **Test your changes**:
   - Manual testing with real Music Assistant server
   - Automated tests (when available)

### Submitting a Pull Request

1. **Push your branch** to your fork:
   ```bash
   git push origin feature/your-feature-name
   ```

2. **Create a pull request** on GitHub with:
   - **Clear title**: Concise description of the change
   - **Description**: What, why, and how
   - **Related issues**: Link to any related issues
   - **Screenshots**: For UI changes
   - **Testing notes**: How to test the changes

3. **PR Template** (example):
   ```markdown
   ## Description
   Implements mDNS server discovery using Zeroconf library.

   ## Changes
   - Added MdnsServerDiscovery class
   - Implemented IServerDiscovery interface
   - Added discovery events (ServerFound, ServerLost)

   ## Related Issues
   Closes #123

   ## Testing
   - Tested with Music Assistant 2.0
   - Verified discovery on local network
   - Tested server connection after discovery

   ## Screenshots
   (if applicable)
   ```

## Code Review Expectations

### For Contributors

- **Be responsive** to feedback and questions
- **Explain your approach** if it's non-obvious
- **Be open to suggestions** and alternative approaches
- **Update the PR** based on feedback

### For Reviewers

Reviews should focus on:

1. **Correctness**: Does it work as intended?
2. **Code Quality**: Is it clean, readable, and maintainable?
3. **Design**: Does it fit the architecture?
4. **Performance**: Are there any performance concerns?
5. **Security**: Are there any security issues?
6. **Testing**: Is it adequately tested?
7. **Documentation**: Is it properly documented?

**Review Checklist:**
- [ ] Code follows style guidelines
- [ ] Public APIs have XML documentation
- [ ] No analyzer warnings
- [ ] Appropriate error handling
- [ ] Logging added where appropriate
- [ ] No hardcoded values (use configuration)
- [ ] Thread-safe where necessary
- [ ] Disposable resources properly disposed

## Commit Messages

### Format

```
<type>(<scope>): <subject>

<body>

<footer>
```

### Types
- **feat**: New feature
- **fix**: Bug fix
- **docs**: Documentation changes
- **style**: Code style changes (formatting, no logic change)
- **refactor**: Code refactoring
- **perf**: Performance improvements
- **test**: Adding or updating tests
- **chore**: Build process or tooling changes

### Examples

```
feat(discovery): add mDNS server discovery

Implements automatic discovery of Sendspin servers on the local network
using Zeroconf library. Supports continuous monitoring and one-time scans.

Closes #123
```

```
fix(connection): handle WebSocket disconnect gracefully

Previously, unexpected disconnects would cause unhandled exceptions.
Now properly catches and logs disconnection events.

Fixes #456
```

### Guidelines
- Use imperative mood ("add" not "added" or "adds")
- Keep subject line under 72 characters
- Separate subject from body with blank line
- Wrap body at 72 characters
- Reference issues in footer

## Documentation

### Code Documentation

- **XML comments** for all public APIs (required)
- **Inline comments** for complex algorithms or non-obvious code
- **README updates** for new features or breaking changes
- **Architecture decisions** documented in code or separate docs

### Documentation Standards

1. **Be Clear**: Use simple, precise language
2. **Be Accurate**: Keep documentation in sync with code
3. **Be Helpful**: Explain *why*, not just *what*
4. **Provide Examples**: Show usage where appropriate
5. **Link to Related Docs**: Reference protocol specs, related classes, etc.

### Example Documentation

```csharp
/// <summary>
/// Synchronizes the client clock with the server using NTP-style measurements.
/// Uses a Kalman filter to estimate clock offset and drift, providing sub-millisecond
/// accuracy for multi-room audio synchronization.
/// </summary>
/// <remarks>
/// The synchronization process follows these steps:
/// 1. Client sends timestamp T1 (client transmitted)
/// 2. Server records T2 (server received) and T3 (server transmitted)
/// 3. Client records T4 (client received)
/// 4. Kalman filter processes the four timestamps to estimate offset and drift
///
/// See: https://github.com/music-assistant/sendspin for protocol details
/// </remarks>
/// <param name="t1">Client transmission timestamp (microseconds)</param>
/// <param name="t2">Server reception timestamp (microseconds)</param>
/// <param name="t3">Server transmission timestamp (microseconds)</param>
/// <param name="t4">Client reception timestamp (microseconds)</param>
public void ProcessMeasurement(long t1, long t2, long t3, long t4)
{
    // Implementation
}
```

## Getting Help

If you need help or have questions:

1. **Check existing documentation**:
   - README.md
   - XML documentation in code
   - Protocol specification

2. **Search existing issues** on GitHub

3. **Ask in discussions** or create a new issue

4. **Join the community**:
   - Music Assistant Discord
   - GitHub Discussions

## Recognition

Contributors will be:
- Listed in project contributors
- Credited in release notes for significant contributions
- Acknowledged in documentation for major features

Thank you for contributing to Sendspin Windows Client!
