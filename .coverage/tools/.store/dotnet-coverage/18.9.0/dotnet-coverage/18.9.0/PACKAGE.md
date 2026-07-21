# dotnet-coverage

The dotnet-coverage tool:
- Enables the cross-platform collection of code coverage data of a running process.
- Provides cross-platform merging of code coverage reports.

## Get started

```shell
dotnet new console -o sample1
cd sample1
dotnet tool install -g dotnet-coverage
dotnet-coverage collect "dotnet run"
```

## Additional information

- [Documentation](https://aka.ms/dotnet-coverage)
- [Samples](https://github.com/microsoft/codecoverage/tree/main/samples/Calculator)
- [Supported OS versions](https://github.com/microsoft/codecoverage/tree/main/docs/supported-os.md)
- [Configuration](https://github.com/microsoft/codecoverage/tree/main/docs/configuration.md)