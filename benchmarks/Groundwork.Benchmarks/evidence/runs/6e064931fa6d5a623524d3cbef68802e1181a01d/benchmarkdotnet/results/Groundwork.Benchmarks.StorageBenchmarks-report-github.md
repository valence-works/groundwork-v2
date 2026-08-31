```

BenchmarkDotNet v0.15.8, macOS Tahoe 26.5.2 (25F84) [Darwin 25.5.0]
Apple M2, 1 CPU, 8 logical and 8 physical cores
.NET SDK 10.0.300
  [Host]     : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a
  DefaultJob : .NET 10.0.8 (10.0.8, 10.0.826.23019), Arm64 RyuJIT armv8.0-a


```
| Method                               | Categories       | Mean       | Error     | StdDev    | Op/s      | Ratio | RatioSD | Gen0    | Gen1   | Allocated | Alloc Ratio |
|------------------------------------- |----------------- |-----------:|----------:|----------:|----------:|------:|--------:|--------:|-------:|----------:|------------:|
| BatchedWrite_Groundwork              | BatchedWrite     | 226.190 μs | 1.5799 μs | 1.4779 μs |   4,421.1 |  1.00 |    0.01 | 44.6777 | 3.9063 | 366.45 KB |        1.00 |
| BatchedWrite_EFCoreCompiledModel     | BatchedWrite     | 339.733 μs | 2.6838 μs | 2.3791 μs |   2,943.5 |  1.50 |    0.01 | 39.0625 | 6.3477 | 320.19 KB |        0.87 |
| BatchedWrite_Dapper                  | BatchedWrite     | 463.294 μs | 2.0826 μs | 1.7391 μs |   2,158.5 |  2.05 |    0.01 | 81.5430 | 5.3711 | 668.03 KB |        1.82 |
|                                      |                  |            |           |           |           |       |         |         |        |           |             |
| CoveredQuery_Dapper                  | CoveredQuery     |  23.532 μs | 0.0935 μs | 0.0829 μs |  42,496.0 |  0.25 |    0.00 |  1.3428 |      - |  11.05 KB |        0.05 |
| CoveredQuery_EFCoreCompiledModel     | CoveredQuery     |  32.571 μs | 0.0886 μs | 0.0692 μs |  30,701.8 |  0.35 |    0.00 |  2.9297 |      - |  24.83 KB |        0.12 |
| CoveredQuery_Groundwork              | CoveredQuery     |  92.896 μs | 0.3399 μs | 0.3179 μs |  10,764.7 |  1.00 |    0.00 | 24.9023 | 0.4883 | 205.01 KB |        1.00 |
|                                      |                  |            |           |           |           |       |         |         |        |           |             |
| PagedQuery_Dapper                    | PagedQuery       |  27.955 μs | 0.1219 μs | 0.1018 μs |  35,771.5 |  0.12 |    0.00 |  0.9460 |      - |   7.91 KB |        0.04 |
| PagedQuery_EFCoreCompiledModel       | PagedQuery       |  36.191 μs | 0.1524 μs | 0.1272 μs |  27,630.9 |  0.16 |    0.00 |  2.5635 |      - |  21.06 KB |        0.11 |
| PagedQuery_Groundwork                | PagedQuery       | 226.380 μs | 1.6278 μs | 1.3593 μs |   4,417.4 |  1.00 |    0.01 | 23.9258 | 0.4883 | 198.15 KB |        1.00 |
|                                      |                  |            |           |           |           |       |         |         |        |           |             |
| PointRead_Dapper                     | PointRead        |   5.091 μs | 0.0507 μs | 0.0450 μs | 196,405.8 |  0.86 |    0.01 |  0.3357 |      - |   2.74 KB |        0.40 |
| PointRead_Groundwork                 | PointRead        |   5.891 μs | 0.0323 μs | 0.0270 μs | 169,755.0 |  1.00 |    0.01 |  0.8316 |      - |   6.85 KB |        1.00 |
| PointRead_EFCoreCompiledModel        | PointRead        |  11.460 μs | 0.1617 μs | 0.1433 μs |  87,262.4 |  1.95 |    0.03 |  0.9766 |      - |   8.09 KB |        1.18 |
|                                      |                  |            |           |           |           |       |         |         |        |           |             |
| UnitOfWorkCommit_Dapper              | UnitOfWorkCommit |  18.754 μs | 0.3217 μs | 0.3009 μs |  53,321.8 |  0.14 |    0.00 |  0.7019 | 0.0916 |   5.75 KB |        0.08 |
| UnitOfWorkCommit_EFCoreCompiledModel | UnitOfWorkCommit |  29.624 μs | 0.2488 μs | 0.2206 μs |  33,756.1 |  0.23 |    0.00 |  1.6479 | 0.1831 |  13.95 KB |        0.20 |
| UnitOfWorkCommit_Groundwork          | UnitOfWorkCommit | 131.655 μs | 1.4157 μs | 1.1822 μs |   7,595.6 |  1.00 |    0.01 |  8.3008 |      - |  69.31 KB |        1.00 |
