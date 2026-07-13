# INSTALL.md — установка MinerU с нуля (Windows + NVIDIA GPU)

Пошаговая инструкция, как поднять **MinerU** для парсинга PDF/DJVU/DOCX в Markdown
с работающим **GPU-движком** (hybrid/vlm через lmdeploy-turbomind), а не медленным
CPU-fallback. Всё проверено на реальной сборке ниже. Команды рассчитаны на запуск
руками в **PowerShell**.

## Что получится в итоге

MinerU 3.4.4, бэкенд `hybrid-engine` считает VLM на GPU через lmdeploy/TurboMind.
На проверке: 11 страниц за секунды (против минут на CPU-движке `transformers`).

## Проверенная конфигурация

| компонент | версия |
|-----------|--------|
| ОС / GPU | Windows, NVIDIA RTX 3070 Laptop 8 ГБ (рабочий дисплей — Intel, NVIDIA под расчёты) |
| Python | 3.12.13 (venv создан через `uv`) |
| MinerU | 3.4.4 |
| torch / torchvision | 2.8.0+cu128 / 0.23.0 |
| lmdeploy | **0.11.1** (важно: `>=0.10.2,<0.12`, см. §5) |
| CUDA Toolkit | 12.8 (под torch cu128) |
| transformers / modelscope | 4.57.6 / 1.38.1 |

Диск: заложи ~15–20 ГБ (пакеты + CUDA Toolkit + модели).

---

## 0. Предварительные требования

1. **Драйвер NVIDIA** установлен и работает — проверь:
   ```powershell
   nvidia-smi
   ```
   Должна показаться карта и версия драйвера. Если нет — поставь свежий драйвер с nvidia.com.

2. **uv** (быстрый менеджер пакетов/venv). Если не стоит:
   ```powershell
   powershell -ExecutionPolicy Bypass -c "irm https://astral.sh/uv/install.ps1 | iex"
   ```
   Проверка: `uv --version`. (Можно и обычный `python -m venv` + `pip`, но ниже — как ставилось на самом деле, через uv.)

---

## 1. Python 3.12 + виртуальное окружение

MinerU/torch/lmdeploy стабильно собраны под **Python 3.12** (не 3.13). Создаём venv:

```powershell
uv venv --python 3.12 D:\Claude\mineru\.venv
```

uv сам скачает Python 3.12, если его нет. Дальше во всех командах используем полный путь
к python из этого venv, чтобы не зависеть от активации:

```powershell
$py = "D:\Claude\mineru\.venv\Scripts\python.exe"
& $py --version   # -> Python 3.12.x
```

---

## 2. Установка MinerU (со всеми экстрами)

```powershell
uv pip install --python $py -U "mineru[all]"
```

`mineru[all]` на Windows тянет и pipeline-модели, и VLM-стек, и — что важно —
**lmdeploy правильной версии** (`mineru[lmdeploy]` пинит `lmdeploy>=0.10.2,<0.12`).
Не ставь `lmdeploy` отдельной командой без версии (см. §5).

Проверка:
```powershell
& $py -m pip show mineru | Select-String "^Version"   # Version: 3.4.4
& "D:\Claude\mineru\.venv\Scripts\mineru.exe" --version
```

---

## 3. Правильный torch под твою CUDA (cu128)

`mineru[all]` может подтянуть torch не под ту CUDA. Ставим сборку **cu128** явно
(совместима с CUDA Toolkit 12.x):

```powershell
uv pip install --python $py `
  --reinstall-package torch --reinstall-package torchvision `
  torch==2.8.0 torchvision==0.23.0 `
  --index-url https://download.pytorch.org/whl/cu128
```

Проверка, что torch видит GPU:
```powershell
& $py -c "import torch; print(torch.__version__, torch.cuda.is_available(), torch.cuda.get_device_name(0))"
# -> 2.8.0+cu128 True NVIDIA GeForce RTX 3070 Laptop GPU
```

> На этом этапе `pipeline`-бэкенд уже поедет на GPU. Но `hybrid`/`vlm` по умолчанию
> захотят lmdeploy, которому нужен **CUDA Toolkit** — §4.

---

## 4. CUDA Toolkit 12.8 (для GPU-движка lmdeploy)

lmdeploy/TurboMind требует установленный **CUDA Toolkit** и переменную `CUDA_PATH`.
Один torch тут не спасает (в нём только рантайм-либы, не полный toolkit).

**Скачать** (локальный инсталлятор ~3.2 ГБ):
- страница: https://developer.nvidia.com/cuda-12-8-0-download-archive → Windows → x86_64 → exe (local)
- прямой файл: `cuda_12.8.0_571.96_windows.exe`

> Если сеть рвёт большие загрузки — качай через BITS с автодокачкой (см. §8, «Обрывы загрузок»).

**Установить.** Проще всего мастером (двойной клик), выбрав вариант «Custom» и оставив
компоненты CUDA (Runtime, Development). Либо тихо из PowerShell:

```powershell
Start-Process "D:\Claude\cuda\cuda_12.8.0_571.96_windows.exe" -ArgumentList "-s","-n" -Verb RunAs -Wait
```

Инсталлятор сам пропишет системную переменную:
```
CUDA_PATH = C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8
```

Проверка после установки:
```powershell
& "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8\bin\nvcc.exe" --version
```

> ⚠️ **Важно:** уже запущенные программы (терминалы, IDE) **не увидят** новый `CUDA_PATH` —
> он подхватывается только процессами, стартовавшими ПОСЛЕ установки. Либо **перезапусти
> терминал** (а надёжнее — перелогинься/перезагрузись), либо задавай `CUDA_PATH` руками
> перед запуском (см. §7).

---

## 5. lmdeploy — версия критична

MinerU 3.4.4 жёстко завязан на API lmdeploy и пинит диапазон:

```
lmdeploy>=0.10.2,<0.12
```

Если поставить свежий (0.12+/0.14) — MinerU упадёт с **обманчивой** ошибкой
`Please install lmdeploy to use the lmdeploy-engine backend`, хотя пакет стоит.
Причина: в новых версиях переехал модуль `lmdeploy.serve.vl_async_engine`, а MinerU
ловит это как `ImportError` и печатает «поставь lmdeploy».

`mineru[all]` из §2 ставит корректную версию сам. Если lmdeploy почему-то не тот —
переставь с пином:

```powershell
uv pip install --python $py "lmdeploy>=0.10.2,<0.12"
& $py -c "import importlib.metadata as m; print('lmdeploy', m.version('lmdeploy'))"   # 0.11.x
```

Проверка, что нужный подмодуль на месте (при выставленном `CUDA_PATH`):
```powershell
$env:CUDA_PATH = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8"
$env:PATH = "$env:CUDA_PATH\bin;$env:PATH"
& $py -c "from lmdeploy.serve.vl_async_engine import VLAsyncEngine; print('VLAsyncEngine OK')"
```

---

## 6. Откуда качать модели (источник)

При первом запуске MinerU докачивает веса. Источник задаётся переменной
`MINERU_MODEL_SOURCE`:

- `huggingface` — по умолчанию; из некоторых сетей CDN HuggingFace нестабилен
  (обрывы, `hf_xet` может вешать загрузку на 0 байт).
- `modelscope` — зеркало Alibaba, часто **надёжнее и стабильнее**. Рекомендуется, если
  HF рвётся.
- `local` — если веса уже лежат локально.

```powershell
$env:MINERU_MODEL_SOURCE = "modelscope"
```

Модели кэшируются в `%USERPROFILE%\.cache\modelscope` (или `...\huggingface`). hybrid
качает лёгкую VLM `MinerU2.5-Pro-2605-1.2B` (~2.3 ГБ) + pipeline-модели.

---

## 7. Первый запуск и проверка GPU-движка

Задаём окружение (CUDA + источник моделей) и гоним тестовый PDF через hybrid:

```powershell
$py    = "D:\Claude\mineru\.venv\Scripts\python.exe"
$exe   = "D:\Claude\mineru\.venv\Scripts\mineru.exe"
$env:CUDA_PATH          = "C:\Program Files\NVIDIA GPU Computing Toolkit\CUDA\v12.8"
$env:PATH               = "$env:CUDA_PATH\bin;$env:CUDA_PATH\libnvvp;$env:PATH"
$env:MINERU_MODEL_SOURCE = "modelscope"

& $exe -p "D:\AI\mineru-test\test.pdf" -o "D:\AI\mineru-test\out" -b hybrid-engine --effort medium
```

**Признак, что GPU-движок завёлся** — в выводе строки:
```
Using lmdeploy-engine as the inference engine for VLM.
lmdeploy device is: cuda, lmdeploy backend is: turbomind
```
Параллельно `nvidia-smi` покажет занятую память и всплески утилизации GPU.

Бэкенды: `-b pipeline` (быстрый CPU/GPU, без VLM), `-b hybrid-engine` (VLM+pipeline,
дефолт), `-b vlm-engine` (только VLM). `--effort medium|high` — только для hybrid.
Полный список флагов: `& $exe --help`.

---

## 8. Траблшутинг (грабли, на которые реально наступили)

**`AssertionError: Can not find $env:CUDA_PATH`**
lmdeploy не видит CUDA Toolkit. Значит: (а) toolkit не установлен (§4), или (б) процесс
стартовал до установки и не унаследовал `CUDA_PATH`. Выставь переменную в текущей сессии
(§7) или перезапусти терминал/перезагрузись.

**`Please install lmdeploy to use the lmdeploy-engine backend`** (хотя lmdeploy стоит)
Не та версия lmdeploy. Нужен `>=0.10.2,<0.12` (§5). Переставь с пином.

**Загрузка моделей висит на 0 байт / `hf_xet` тормозит / read timeout**
Нестабильный HF CDN. Варианты: переключись на `MINERU_MODEL_SOURCE=modelscope` (§6);
отключи xet — `$env:HF_HUB_DISABLE_XET="1"`.

**Обрывы больших загрузок (CUDA-инсталлятор, колёса pip)**
Если pip/HTTP рвётся на середине — качай **резюмируемо через BITS**, потом ставь локально:
```powershell
# скачать файл с автодокачкой
Start-BitsTransfer -Source "<URL>" -Destination "D:\Claude\dl\file" -Asynchronous -DisplayName job1
# следить: Get-BitsTransfer -Name job1  ->  BytesTransferred / BytesTotal
# на обрыве (TransientError): Get-BitsTransfer -Name job1 | Resume-BitsTransfer
# по готовности (Transferred): Get-BitsTransfer -Name job1 | Complete-BitsTransfer
```
Для колеса pip затем: `uv pip install --python $py --no-deps "D:\Claude\dl\<wheel>.whl"`.

**hybrid ловит OOM на 8 ГБ**
На 8 ГБ VRAM hybrid обычно влезает (VLM 1.2B, ~2.5–6 ГБ). Если всё же OOM — освободи GPU
(закрой другие потребители VRAM) или используй `-b vlm-engine` (одна модель) / `-b pipeline`.

**transformers вместо lmdeploy (GPU на 30–40%, одно ядро CPU 100%)**
Значит lmdeploy недоступен (не установлен / не тот / нет CUDA_PATH), и MinerU откатился на
`transformers` — он гонит декод VLM в один поток под GIL, GPU голодает. Почини §4–§5.

---

## Приложение: как это использует GUI llmocr

Приложение `D:\Claude\llmocr` запускает mineru как сервер (`mineru-api`) и клиент.
Чтобы не зависеть от того, унаследовал ли процесс машинный `CUDA_PATH`, в его
`config.json` есть поле `CudaPath` — приложение само вписывает `CUDA_PATH` (+ `bin` в
PATH) в окружение mineru. Так что после установки Toolkit достаточно **перезапустить
приложение** — переменные окружения системы трогать не нужно.
