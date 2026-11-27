창작마당 제목, 소개와 이미지는 Steam에서 관리합니다. 모드 업데이트는 `build/pack.ps1`로 빌드한 뒤 `workshop/change-note.txt`에 변경 사항을 적고 프로젝트 루트에서 실행하세요.

```powershell
dotnet run --project build/publish -- workshop --publish
```

`--publish`를 생략하면 로컬 배포 파일과 Steam 연결을 점검합니다.
