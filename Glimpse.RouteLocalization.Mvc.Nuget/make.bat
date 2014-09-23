mkdir input\lib\net40
del /Q input\lib\net40\*.*

msbuild .\..\Glimpse.RouteLocalization.Mvc\Glimpse.RouteLocalization.Mvc.csproj /p:Configuration=Release;OutputPath=.\..\Glimpse.RouteLocalization.Mvc.Nuget\input\lib\net40

mkdir output
..\.nuget\nuget.exe pack /o output .\Glimpse.RouteLocalization.Mvc.nuspec