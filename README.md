# Spire Coach - AlphaDefect (첨탑 코치 / スパイアコーチ)

## 한국어

슬더스 2 싱글플레이를 하면서 참고하려고 만든 모드입니다. 지금 가능한 선택을 AlphaDefect가 선호하는 순서로 보여 줍니다. 퍼센트가 높을수록 AI가 선호하는 선택입니다.

전투에서는 어떤 카드를 누구한테 쓸지, 포션을 쓸지, 턴을 끝낼지 추천합니다. 카드 보상에서는 어떤 카드를 집을지, 그냥 넘길지 비교합니다. 지도에서는 다음 방을, 상점에서는 물건을 살지, 카드를 제거할지, 골드를 아낄지 추천합니다.

카드 효과나 비용이 패치로 바뀌면, 새 밸런스로 다시 학습한 모델이 모드에 반영될 때까지 추천이 실제 플레이와 잘 맞지 않을 수 있습니다.

원본 논문에서 캐릭터별 모델 성능을 마지막으로 확인한 게임 버전  
v0.111.0 리젠트  
v0.113.1 아이언클래드, 사일런트, 디펙트, 네크로바인더, 풀

모드의 추천 표시를 확인한 버전은 정규판 v0.111.1(5명), 베타판 v0.114.0(풀 포함 6명)입니다. 카드, 유물, 포션 이름은 게임 언어를 따르고 코치 메뉴와 안내는 한국어입니다.

구독 후 모드를 활성화하고 게임을 재시작하세요. 처음 한 번만 F7을 누르거나 화면의 ‘모델 어휘 받기’를 눌러 주세요. 이후에는 오프라인으로 쓸 수 있습니다.

F8 또는 R3 추천 창 켜고 끄기, F6 상세 설명, F9 다음 페이지, F10 설정  
설정에서 추천 창과 상세 설명의 단축키를 바꿀 수 있습니다. 스팀덱에서는 터치로도 조작할 수 있습니다.

AlphaDefect 논문을 쓰신 Qiyue Chen, Taian Yu, Junpei Komiyama, Haobo Huang 님께 감사드립니다. 모델과 코드를 MIT 라이선스로 공개해 주신 덕분에 실제 게임에서 AI의 선택을 보며 배울 수 있게 됐습니다.

GitHub https://github.com/hmty-CHEN/AlphaDefect-Public
AlphaDefect: Expert-Level AI for Slay the Spire 2 (arXiv)[[arxiv.org]](https://arxiv.org/abs/2611.31076)


## English

I made Spire Coach as a reference for my own single-player runs in Slay the Spire 2. It ranks the choices available to you. A higher percentage means AlphaDefect prefers that option.

In combat, see which card to play and who to target, when to use a potion, or when to end your turn. The coach also suggests which rewards to take or skip, which room to visit next, and when to buy something, remove a card or save your gold.

If a patch changes a card’s effect or cost, the advice may be less reliable until the mod includes a model trained on the new balance.

Last game version the AlphaDefect paper evaluated for each character  
v0.111.0 Regent  
v0.113.1 Ironclad, Silent, Defect, Necrobinder, Fool

I checked that the mod displays recommendations for all five characters on main v0.111.1 and all six, including the Fool, on beta v0.114.0. Card, relic and potion names follow your game language. The coach menus and prompts are in Korean.

Subscribe, enable the mod and restart the game. On first launch, press F7 or tap ‘모델 어휘 받기’ to download the model vocabulary. After that, you can play offline.

F8 or R3 to toggle the coach, F6 to toggle details, F9 for the next page, F10 for settings  
You can rebind the coach and detail toggles in settings. Touch controls are available on Steam Deck.

Thank you to the authors of the AlphaDefect paper, Qiyue Chen, Taian Yu, Junpei Komiyama and Haobo Huang. Releasing the models and code under the MIT license made it possible for players to learn from the AI during their own runs.

GitHub https://github.com/hmty-CHEN/AlphaDefect-Public
AlphaDefect: Expert-Level AI for Slay the Spire 2 (arXiv)[[arxiv.org]](https://arxiv.org/abs/2611.31076)


## 日本語

自分で『Slay the Spire 2』のシングルプレイを遊ぶときの参考に作ったMODです。今選べる行動を、AlphaDefectが選びやすい順に表示します。パーセントが高いほど、AIがその選択を好んでいます。

戦闘では、どのカードを誰に使うか、ポーションを使うか、ターンを終了するかを提案します。カード報酬を取るか見送るか、マップで次にどの部屋へ進むか、ショップで買い物をするか、カードを削除するか、ゴールドを温存するかも比較できます。

パッチでカードの効果やコストが変わると、新しいバランスで再学習したモデルがMODに反映されるまで、おすすめが実際のプレイに合わないことがあります。

AlphaDefectの論文でキャラクターごとにモデルの性能を最後に評価したゲームバージョン  
v0.111.0 リージェント  
v0.113.1 アイアンクラッド、サイレント、ディフェクト、ネクロバインダー、フール

MODのおすすめ表示は、通常版v0.111.1で全5キャラクター、ベータ版v0.114.0でフールを含む全6キャラクターで確認しました。カード、レリック、ポーションの名前はゲームの言語設定に従います。コーチのメニューと案内は韓国語です。

サブスクライブしてMODを有効にしたら、ゲームを再起動してください。初回だけF7を押すか、画面の「모델 어휘 받기」をタップしてモデルの語彙ファイルをダウンロードしてください。以降はオフラインで使えます。

F8またはR3でコーチの表示切替、F6で詳細の表示切替、F9で次のページ、F10で設定  
コーチと詳細表示のキー割り当ては設定で変更できます。Steam Deckではタッチ操作もできます。

AlphaDefectの論文を執筆されたQiyue Chenさん、Taian Yuさん、Junpei Komiyamaさん、Haobo Huangさんに感謝します。モデルとコードをMITライセンスで公開してくださったおかげで、実際に遊びながらAIの選択から学べるようになりました。

GitHub [https://github.com/hmty-CHEN/AlphaDefect-Public](https://github.com/hmty-CHEN/AlphaDefect-Public)
[AlphaDefect: Expert-Level AI for Slay the Spire 2 (arXiv)](https://arxiv.org/abs/2611.31076)
