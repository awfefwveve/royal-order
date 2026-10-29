# Core 테스트 (유니티 밖에서 실행)

폴더 이름 끝의 `~` 때문에 유니티는 이 폴더를 무시합니다. `Stubs.cs`는 유니티 API를 흉내 내는 테스트 전용 파일입니다.

.NET 8 SDK가 있으면 프로젝트 루트에서:

```
csc 대신 아래처럼 dotnet의 Roslyn으로 컴파일:
dotnet <sdk>/Roslyn/bincore/csc.dll -langversion:9.0 -out:t.dll -r:<net8 ref dll들> CoreTests~/Stubs.cs Assets/KnifeWar/Core/*.cs CoreTests~/GameplayTests.cs
```

MonsterSimTests.cs와 GameplayTests.cs는 각각 Main이 있으니 하나씩 컴파일하세요.
