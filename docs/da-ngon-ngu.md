# Luồng Đa ngôn ngữ, AI dịch và tóm tắt di sản, nghe thuyết minh (Backend)

Phần backend của việc đưa nội dung di sản công khai sang **năm ngôn ngữ**: tiếng Việt (bản gốc), tiếng Anh, tiếng Hàn, tiếng Trung giản thể, tiếng Nhật. Quản trị hệ thống bấm
"Dịch và tóm tắt bằng AI" ở một di sản; AI dịch mọi phần chữ của di sản và viết tóm tắt (bản ngắn để nghe, bản chi tiết để đọc); bản **tiếng Việt (tóm tắt) và tiếng Anh phải được
quản trị hệ thống duyệt**, còn bản **tiếng Hàn, Trung, Nhật hiện ngay và luôn kèm nhãn "bản dịch máy"**. Khách (web học tập, app) gọi các API công khai kèm mã ngôn ngữ và nhận nội dung
đã lưu sẵn, **không gọi AI khi xem**. "Nghe thuyết minh" đọc bản tóm tắt ngắn bằng giọng đọc của thiết bị hoặc trình duyệt: backend **không tạo, không lưu, không tải tệp âm thanh**,
chỉ cấp chữ và mã giọng đọc.

| Mục | Giá trị |
|---|---|
| Chức năng | Chọn ngôn ngữ hiển thị · Đọc tóm tắt di sản theo ngôn ngữ · Nghe thuyết minh di sản (phần chữ) · Yêu cầu AI dịch và tóm tắt di sản · Duyệt bản dịch và tóm tắt tiếng Việt, tiếng Anh |
| Màn hình phục vụ | Web học tập và app (phần công khai): Chọn ngôn ngữ, Chi tiết di sản, Tóm tắt và nghe thuyết minh, Tìm kiếm, Bản đồ, Dòng thời gian. Web quản trị (quản trị hệ thống): tab Bản dịch và tóm tắt của Biên tập di sản, Duyệt bản dịch và tóm tắt, cột và bộ lọc tình trạng bản dịch ở Quản lý di sản |
| Bảng (CSDL v4, migration `ThemBanDichVaTomTatDiSan`) | `heritage_translations`, `timeline_translations`, `event_translations`, `media_translations`, `heritage_summaries`; ghi `ai_generations` (đã có cột `heritage_id` và hai loại tác vụ di sản từ luồng bài học), `audit_logs`; đọc `heritages`, `timelines`, `events`, `media`, `users` |
| Lớp mới | `Domain/Rules/Languages`, `TranslationRules`; `Domain/Heritages/HeritageTranslation` (5 thực thể); `Application/Translations/` (`HeritageTranslationService`, `HeritageLocalizer`, `PublicSummaryService`, `HeritageAiRunner`, hợp đồng `IHeritageAiClient`, `TranslationOutdated`, DTO, thông điệp); `Infrastructure/Ai/SimulatedHeritageAiClient`, `HttpHeritageAiClient`; `Api/Controllers/AdminTranslationsController` |
| Nguồn | `doc/plan/spec` (use case, màn hình, quy tắc ngôn ngữ, bản dịch, tóm tắt, thông báo) · `doc/database/dsvhvn-v4.dbml` |

## 1. Năm ngôn ngữ

| Mã (API, giao diện) | Mã lưu trong CSDL (`language_code`) | Tên hiển thị | Mã giọng đọc của thiết bị | Cách có nội dung |
|---|---|---|---|---|
| `vi` | `vi` | Tiếng Việt | `vi-VN` | Bản gốc trong bảng chính; chỉ **tóm tắt** có dòng riêng |
| `en` | `en` | English | `en-US` | AI dịch, **quản trị hệ thống duyệt** |
| `ko` | `ko` | 한국어 | `ko-KR` | AI dịch, bản dịch máy, không duyệt |
| `zh-Hans` | `zh` | 简体中文 | `zh-CN` | AI dịch, bản dịch máy, không duyệt |
| `ja` | `ja` | 日本語 | `ja-JP` | AI dịch, bản dịch máy, không duyệt |

- Không có tiếng Trung phồn thể và không có ngôn ngữ nào khác. API nhận cả mã ngắn `zh` và dạng vùng của trình duyệt: `en-US`, `ko-KR`, `zh-CN`, `zh-SG`, `zh-Hans-SG`; còn
  `zh-TW`, `zh-HK`, `zh-Hant` **không thuộc** năm ngôn ngữ. `Languages.TryParse` đọc mã, `Languages.FromAcceptLanguage` đọc tiêu đề `Accept-Language` (xếp theo trọng số `q`, bỏ mục `q=0`).
- `GET /api/v1/public/languages` trả danh sách trên `{ code, nativeName, speechTag, isSource, isMachineOnly }` cho bộ chọn ngôn ngữ (mỗi ngôn ngữ viết bằng chính nó, không dùng cờ).
  Danh mục cố định nên trình duyệt được lưu tạm 1 giờ.
- **Chữ giao diện** (tên 34 tỉnh, nhãn, nút, thông báo) nằm trong tệp chuỗi của web học tập và app, **không** nằm ở backend và không lưu theo di sản. Backend chỉ trả mã thông điệp
  (`errorCode`) và các cờ; giao diện dịch ra câu theo ngôn ngữ đang chọn.
- Ngôn ngữ mặc định lần đầu mở (ngôn ngữ trình duyệt hoặc thiết bị nếu thuộc năm ngôn ngữ, không thì tiếng Anh) và việc ghi nhớ lựa chọn là việc của giao diện (lưu trên thiết bị).
  Backend có sẵn `Languages.DefaultFor(...)` cho cùng quy tắc nhưng không lưu lựa chọn ở máy chủ.

## 2. Dữ liệu

Mỗi bảng có đúng một dòng cho mỗi (bản gốc, ngôn ngữ) (UNIQUE). Tên cột, kiểu, CHECK, khóa ngoại theo `dsvhvn-v4.dbml`.

| Bảng | Nội dung | Trạng thái và ràng buộc |
|---|---|---|
| `heritage_translations` | `name`, `short_description`, `content`, `address`, `status`, `is_outdated`, `ai_generation_id`, `reviewed_by`, `reviewed_at`. **Đơn vị duyệt** của bản dịch: trạng thái áp cho cả bản dịch giai đoạn, sự kiện, chú thích ảnh cùng ngôn ngữ | `en`: `PENDING`, `APPROVED`, `REJECTED`; `ko`, `zh`, `ja`: luôn `MACHINE`; không có dòng `vi` (CHECK `CK_heritage_translations_status`, `..._language_code`) |
| `timeline_translations` | tên và mô tả giai đoạn | `language_code <> 'vi'`; xóa giai đoạn thì xóa bản dịch (Cascade) |
| `event_translations` | tên và mô tả sự kiện | như trên, Cascade theo sự kiện |
| `media_translations` | chú thích ảnh (ghi công không dịch) | như trên, Cascade theo ảnh |
| `heritage_summaries` | `short_text` (≤ 2000 ký tự, để nghe), `detailed_text` (để đọc), `status`, `is_outdated`, `ai_generation_id`, `reviewed_by`, `reviewed_at` | `vi`, `en`: `PENDING`, `APPROVED`, `REJECTED`; `ko`, `zh`, `ja`: luôn `MACHINE` (CHECK `CK_heritage_summaries_status`) |

Trạng thái: `MACHINE` bản dịch máy (hiện ngay, kèm nhãn) · `PENDING` chờ duyệt (chưa hiện) · `APPROVED` đã duyệt (hiện) · `REJECTED` bị từ chối (không hiện, chờ dịch lại).
"Cần dịch lại" **không phải** trạng thái mà là cờ `is_outdated` (mục 7).

- Khóa ngoại tới `ai_generations`, `users`, `heritages` đều **Restrict** (không xóa cứng di sản đã có bản dịch); bốn bảng con của nội dung Cascade theo bản gốc. Cột `is_outdated` mặc định 0.
- `address` của bản dịch luôn trống do AI không dịch địa chỉ (giữ như văn bản xếp hạng); cột để dành cho quản trị nhập tay. Nguồn tham chiếu, dòng ghi công ảnh và ghi công mô hình 3D không dịch, không có bảng.
- Migration `ThemBanDichVaTomTatDiSan` chỉ **thêm** năm bảng, không sửa bảng cũ nên chạy được trên CSDL có dữ liệu. Số liệu sau migration trên SQL Server: **33 bảng**, 303 cột, 52 khóa ngoại
  (4 Cascade), 35 CHECK, 15 UNIQUE thường (xem [co-so-du-lieu.md](co-so-du-lieu.md) mục 7).
- Nhật ký thao tác thêm hai mã `TRANSLATION_APPROVED`, `TRANSLATION_REJECTED` (`audit_logs.action` không có CHECK nên không cần migration); đối tượng là `heritage_translations` hoặc `heritage_summaries`.

## 3. API công khai (tiền tố `/api/v1/public`, không cần đăng nhập)

Mọi endpoint về di sản nhận tham số `lang` (mã ở mục 1) **hoặc** tiêu đề `Accept-Language`. Thứ tự: `lang` nếu có và thuộc năm ngôn ngữ → tiêu đề `Accept-Language` (ngôn ngữ đầu tiên thuộc
năm ngôn ngữ) → tiếng Việt. Có `lang` mà **không thuộc năm ngôn ngữ** (kể cả `zh-TW`) thì **bỏ qua và trả tiếng Việt**, không báo lỗi và không lùi về tiêu đề. Phản hồi có `Vary: Accept-Language`.
**Học sinh đã đăng nhập luôn nhận tiếng Việt** dù gửi `lang` nào (khu học và trang công khai trong khu học chỉ có tiếng Việt); giáo viên và quản trị gọi như khách.

| Method | Đường dẫn | Thêm so với luồng Khám phá di sản |
|---|---|---|
| GET | `/languages` | 200 danh sách năm ngôn ngữ (mục 1) |
| GET | `/home` | `lang`: các thẻ nổi bật theo ngôn ngữ |
| GET | `/heritages` | `lang`: thẻ theo ngôn ngữ; **từ khóa khớp cả bản tiếng Việt lẫn bản dịch hiện hành của ngôn ngữ đã chọn** (mục 5) |
| GET | `/heritages/map` | `lang`: ghim và popup theo ngôn ngữ, cùng cách tìm |
| GET | `/heritages/{slug}` | `lang`: tên, mô tả ngắn, nội dung, tên giai đoạn rút gọn, chú thích ảnh theo ngôn ngữ; thêm `localization` và `hasSummary` |
| GET | `/heritages/{slug}/timeline` | `lang`: tên di sản, tên và mô tả giai đoạn, tên và mô tả sự kiện, chú thích ảnh sự kiện; thêm `localization` |
| GET | `/heritages/{slug}/summary` | **Mới.** Tóm tắt theo ngôn ngữ: `{ heritageId, slug, name, localization, shortText, detailedText, narration }`; 404 `SUMMARY_UNAVAILABLE` khi chưa có tóm tắt tiếng Việt đã duyệt. `narration` (mục 14) là thuyết minh tạo sẵn `{ audioUrl, durationMs, voice, sentences: [{ text, startMs, endMs }] }` của đúng bản tóm tắt đang trả, hoặc `null` |

Các cờ trả kèm (không có thì giao diện không biết mà gắn nhãn, nên **không bỏ**):

- `localization` (chi tiết, timeline, tóm tắt): `{ requestedLanguage, language, isFallback, isMachineTranslated }`. `language` là ngôn ngữ nội dung đang hiện; `isFallback` bật khi chưa có bản hiện hành
  nên đang hiện tiếng Việt (giao diện hiện **một thông báo cho cả trang** "Nội dung này chưa có bản {ngôn ngữ}. Đang hiển thị bản tiếng Việt."); `isMachineTranslated` bật với tiếng Hàn, Trung, Nhật
  (giao diện gắn nhãn "Bản dịch máy, có thể chưa chính xác." cạnh nội dung, **không bao giờ bỏ**).
- Mỗi thẻ ở danh sách, tìm kiếm, trang chủ và mỗi ghim bản đồ có `isFallback`, `isMachineTranslated` **riêng**: tên và mô tả ngắn của thẻ lùi về tiếng Việt từng thẻ, giao diện hiện thông báo một lần ở đầu danh sách.
- `hasSummary` ở chi tiết: di sản có tóm tắt tiếng Việt đã duyệt. Là `false` thì nút "Tóm tắt" và "Nghe" báo "Di sản này chưa có tóm tắt. Bạn vẫn có thể đọc nội dung đầy đủ bên dưới." —
  **không hiện cổng quảng cáo, không mở khóa, không tính lượt** (luồng quảng cáo làm sau, khóa tóm tắt chưa có ở luồng này).

## 4. Quy tắc hiển thị theo ngôn ngữ

Bản dịch **hiện hành** của một ngôn ngữ là dòng `heritage_translations` có `APPROVED` (tiếng Anh) hoặc `MACHINE` (Hàn, Trung, Nhật). Dòng `PENDING` và `REJECTED` không hiện.

1. **Tiếng Việt** luôn là nội dung gốc.
2. **Tiếng Anh** chỉ hiện sau khi duyệt. Chưa dịch, chờ duyệt hay bị từ chối đều cho cùng một kết quả: nội dung tiếng Việt kèm `isFallback = true`. Trang không bao giờ trống hay lỗi.
3. **Tiếng Hàn, Trung, Nhật** hiện ngay sau khi AI dịch, `isMachineTranslated = true`.
4. Giai đoạn, sự kiện, chú thích ảnh đi theo trạng thái của dòng chính cùng ngôn ngữ (duyệt hay không duyệt cả di sản một lần). Có dòng chính mà thiếu bản dịch của **một phần** (quản trị xóa tên giai đoạn
   đã dịch, hay phần đó mới thêm sau khi dịch) thì chỉ phần đó lùi về tiếng Việt, không bật `isFallback`.
5. Bản đang **cần dịch lại** (`is_outdated`) **vẫn hiện bản cũ** tới khi quản trị dịch lại.
6. Dịch lại thay chính dòng cũ (mỗi di sản và ngôn ngữ một dòng): tiếng Anh và tóm tắt tiếng Việt về `PENDING` nên **bản cũ ngừng hiện** cho tới khi bản mới được duyệt; bản máy hiện ngay bản mới.
7. Tên 34 tỉnh, vùng, loại, cấp xếp hạng, thời kỳ lọc **không** dịch ở backend (lấy từ tệp chuỗi). Địa chỉ, nguồn tham chiếu, ghi công ảnh **không dịch**.
8. Xem trước bản nháp (`/api/v1/preview/heritages/{mã}` và `/timeline`) cũng nhận `lang` và dùng cùng quy tắc, nên quản trị xem được bản Hàn, Trung, Nhật của bản nháp.

## 5. Tìm kiếm theo ngôn ngữ

Từ khóa 2-100 ký tự như trước ([kham-pha-di-san.md](kham-pha-di-san.md) mục 3). Khi `lang` là tiếng Việt: đúng như cũ (gấp chữ, khớp đầu từ, không dấu). Khi `lang` là ngôn ngữ khác, di sản khớp nếu
**khớp bản tiếng Việt (như cũ) hoặc khớp bản dịch hiện hành của ngôn ngữ đã chọn**:

- Bản dịch không có cột gấp sẵn (chữ Hàn, Trung, Nhật không gấp được như tiếng Việt). Từ khóa được tách theo dấu cách (tối đa 8 từ); mỗi từ phải có trong **tên, mô tả ngắn hoặc nội dung** của bản dịch
  (LIKE `%từ%`, không phân biệt hoa thường nhờ collation), di sản phải có đủ mọi từ.
- Ký tự đại diện của LIKE trong từ khóa (`%`, `_`, `[`, `\`) được **escape**, là chữ thường; mẫu đi qua tham số của EF Core.
- Bản dịch **chưa hiện** (chờ duyệt, bị từ chối) thì không tìm ra bằng chữ của chính nó. Tiếng Việt luôn khớp.
- Xếp kết quả: khớp nguyên cụm trong tên (tiếng Việt không dấu hoặc tên đã dịch) trước, rồi cấp xếp hạng, rồi tên tiếng Việt.

## 6. Tóm tắt ngắn và chi tiết, nghe thuyết minh

- Mỗi di sản có hai tóm tắt cho mỗi ngôn ngữ: **bản ngắn** (`shortText`, để nghe, khoảng 80-120 từ tiếng Việt, đọc to trong khoảng một phút) và **bản chi tiết** (`detailedText`, để đọc,
  khoảng 250-400 từ). AI chỉ dùng chữ đã có của di sản (mô tả, nội dung, giai đoạn), không thêm dữ kiện và không chép nguyên bài; bản các ngôn ngữ khác **dịch từ tóm tắt tiếng Việt**, không đo lại độ dài.
- **Lưu sẵn**, dùng lại cho mọi lượt xem; chỉ tạo lại khi quản trị bấm dịch lại.
- **Tóm tắt tiếng Việt chưa được duyệt thì di sản chưa có tóm tắt ở bất kỳ ngôn ngữ nào**, kể cả bản Hàn, Trung, Nhật (dịch từ bản tiếng Việt nên chỉ hiện sau khi bản tiếng Việt được duyệt, kèm nhãn).
- Ngôn ngữ yêu cầu chưa có tóm tắt hiện hành (tiếng Anh chờ duyệt) thì trả **tóm tắt tiếng Việt đã duyệt** với `localization.isFallback = true` và `language = "vi"`: giao diện hiện thông báo thiếu bản dịch
  và nghe bằng **giọng tiếng Việt** (không đọc bằng giọng của ngôn ngữ khác).
- **Nghe thuyết minh**: web và app lấy `shortText` rồi đọc bằng giọng có sẵn của thiết bị hoặc trình duyệt, chọn giọng theo `speechTag` của `/languages` (hoặc `localization.language` khi lùi về tiếng Việt). Phát,
  tạm dừng, dừng, đổi tốc độ (0,75x, 1x, 1,25x, 1,5x) và tô sáng câu đang đọc là việc của giao diện. Thiết bị không có giọng của ngôn ngữ đó thì giao diện báo, gợi ý cài giọng đọc và vẫn hiện đủ chữ.
  Backend **không** hỏi đáp bằng giọng nói, không thu âm.
- Tóm tắt cho khách khác với tóm tắt nội dung bài học cho giáo viên ([bai-hoc-theo-cap.md](bai-hoc-theo-cap.md)): hai tác vụ AI riêng, nhật ký riêng.

## 7. Cần dịch lại

Khi **phần chữ tiếng Việt đã được dịch** bị sửa, hệ thống bật `is_outdated = 1` cho **mọi** bản dịch và tóm tắt của di sản (bằng một lệnh cập nhật, không đổi `updated_at`, bản cũ vẫn hiện):

| Sửa | Đánh dấu |
|---|---|
| Tên, mô tả ngắn, địa chỉ hoặc nội dung của di sản (chỉ khi giá trị **thực sự đổi**) | có |
| Thêm giai đoạn, hoặc sửa tên, mô tả giai đoạn (đổi năm, thứ tự thì không) | có |
| Thêm sự kiện, hoặc sửa tên, mô tả sự kiện | có |
| Thêm ảnh có chú thích, hoặc sửa chú thích ảnh | có |
| Đổi loại, cấp xếp hạng, năm công nhận, tỉnh, tọa độ, ảnh đại diện, thứ tự | không |

Màn hình Biên tập di sản nhận `outdatedCount` và `outdatedNotice` ("Nội dung tiếng Việt đã được sửa sau khi dịch. {n} bản dịch và tóm tắt cần dịch lại; …"). Dịch lại thì cờ về 0.
Danh sách Quản lý di sản và chi tiết di sản (quản trị) có `translationState`: `NONE` (chưa dịch) · `OUTDATED` (có bản cần dịch lại) · `PENDING` (có bản chờ duyệt) · `APPROVED` (đã dịch, không còn bản chờ hay cần dịch lại);
danh sách lọc được bằng `translationState`. Chi tiết di sản dùng `translationState = NONE` để nhắc "Dịch và tóm tắt bằng AI" sau khi xuất bản.

## 8. API quản trị (tiền tố `/api/v1/admin`, quyền **ADMIN**)

Mã ngôn ngữ trong đường dẫn: `vi` (chỉ tóm tắt), `en`, `ko`, `zh-Hans` (nhận cả `zh`), `ja`. Khuôn phản hồi chung `{ success, data, message, errorCode, traceId, errors }`.

| Method | Đường dẫn | Body / query | Thành công | Lỗi chính |
|---|---|---|---|---|
| GET | `/heritages/{id}/translations` | — | 200 `Overview`: `canGenerate`, `cannotGenerateReason`, `outdatedCount`, `outdatedNotice`, `translations[]` (en, ko, zh-Hans, ja) và `summaries[]` (vi, en, ko, zh-Hans, ja); mỗi ô `{ kind, language, languageLabel, status, isOutdated, label, createdAt, updatedAt, reviewedAt, reviewedBy, reviewedByName }`. `label`: Chưa có · Bản dịch máy · Chờ duyệt · Đã duyệt · Bị từ chối · Cần dịch lại | 404 |
| POST | `/heritages/{id}/translations/generate` | — | 200 `Overview` mới, "Đã dịch và tóm tắt di sản. …" | 404 · 409 `TRANSLATION_BUSY` (đang có lần dịch chạy) · 422 `BLOCKED` (chưa có nội dung tiếng Việt, hoặc di sản đang bị ẩn) · 503 `AI_UNAVAILABLE` |
| GET | `/heritages/{id}/translations/{language}/translation` | `en`, `ko`, `zh-Hans`, `ja` | 200 `{ …, status, isOutdated, reviewedAt, reviewedByName, original, translated }`; `original` và `translated` cùng hình dạng `{ name, shortDescription, content, address, timelines[], events[], media[] }` (phần chưa dịch là `null`) | 400 (`vi` hoặc mã lạ) · 404 |
| PUT | `/heritages/{id}/translations/en/translation` | `{ name?, shortDescription?, content?, address?, timelines?: [{ id, name?, description? }], events?: [...], media?: [{ id, caption? }] }` | 200 như GET | 400 (`REQUIRED`, `INVALID`: quá dài, mã phần không thuộc di sản) · 404 · 422 (Hàn, Trung, Nhật là bản dịch máy, không sửa tay) |
| GET | `/heritages/{id}/translations/{language}/summary` | `vi`, `en`, `ko`, `zh-Hans`, `ja` | 200 `{ …, original, viSummary, summary: { shortText, detailedText } }` | 400 · 404 |
| PUT | `/heritages/{id}/translations/{language}/summary` | `{ shortText?, detailedText? }` (`vi` hoặc `en`) | 200 như GET | 400 · 404 · 422 (Hàn, Trung, Nhật) |
| POST | `/heritages/{id}/translations/{language}/approve` | `{ kind: "TRANSLATION" \| "SUMMARY" \| "BOTH" }` | 200 `{ overview, machineTranslationsRefreshed, warning }` | 400 · 404 · 422 `BLOCKED` |
| POST | `/heritages/{id}/translations/{language}/reject` | `{ kind, reason? }` (`reason` ≤ 500 ký tự) | 200 như duyệt | 400 · 404 · 422 `BLOCKED` |
| GET | `/translations` | `tab` (`PENDING` mặc định, `APPROVED`, `MACHINE`, `REJECTED`), `heritageId`, `language`, `kind` (`TRANSLATION`, `SUMMARY`), `page`, `pageSize` | 200 trang `{ heritageId, heritageName, slug, kind, language, languageLabel, status, isOutdated, requestedAt, reviewedAt }`, mới yêu cầu trước | 400 |
| GET | `/translations/counts` | — | 200 `{ pending, approved, machine, outdated }` (đếm cả bản dịch lẫn tóm tắt của di sản chưa xóa) | — |

- Duyệt chỉ áp cho bản **đang chờ duyệt**. `kind`: `TRANSLATION` (chỉ `en`), `SUMMARY` (`vi` hoặc `en`), `BOTH` (cả hai của ngôn ngữ đó; với `vi` là tóm tắt). Chọn `BOTH` thì bỏ qua phần không có hoặc không còn chờ;
  không còn gì chờ thì 422 "Không có bản nào đang chờ duyệt ở ngôn ngữ này." Chọn một đối tượng cụ thể mà nó đã duyệt, đã bị từ chối hoặc không có thì báo đúng lý do (422, 404).
- **Hàn, Trung, Nhật không có bước duyệt**: duyệt hoặc từ chối đều 422. Tiếng Việt là bản gốc nên `TRANSLATION` với `vi` là 400.
- Từ chối áp cho bản chờ duyệt **và bản đã duyệt** (gỡ một bản đã hiện mà còn sai khỏi trang công khai); lý do (không bắt buộc) **chỉ ghi vào `new_value` của nhật ký**, không có cột riêng.
  Từ chối là "yêu cầu dịch lại": bấm lại "Dịch và tóm tắt bằng AI". Sửa tại chỗ một bản đã bị từ chối hoặc đã duyệt thì bản đó **quay về chờ duyệt** (bỏ người và thời điểm duyệt).
- Duyệt ghi `reviewed_by`, `reviewed_at` và một dòng nhật ký `TRANSLATION_APPROVED` cho **mỗi đối tượng** (`new_value`: `heritageId`, `heritage`, `language`, `kind`); từ chối ghi `TRANSLATION_REJECTED` kèm `reason`.
- **Duyệt bản dịch tiếng Anh thì bản Hàn, Trung, Nhật đã có được dịch lại từ bản tiếng Anh đã duyệt** (chỉ phần bản dịch nội dung; tóm tắt các ngôn ngữ này dịch từ tóm tắt tiếng Việt khi bấm dịch).
  Việc này chạy ngay trong lời gọi duyệt (`machineTranslationsRefreshed = true`); Dịch vụ AI lỗi thì **lần duyệt vẫn giữ**, bản máy cũ giữ nguyên và `warning` nói rõ để bấm dịch lại.
  Cũng vì thế, bấm "Dịch và tóm tắt bằng AI" khi tiếng Anh đã duyệt thì tiếng Hàn, Trung, Nhật dịch từ bản tiếng Anh đã duyệt (trước khi nó bị thay), tiếng Anh dịch từ tiếng Việt.
- `GET /heritages` (Quản lý di sản) thêm bộ lọc `translationState`; `GET /heritages/{id}` thêm `translationState` (mục 7).

## 9. Quy trình "Dịch và tóm tắt bằng AI"

1. Chỉ **quản trị hệ thống**, ở di sản **Nháp hoặc Đã xuất bản** chưa xóa có tên và có mô tả ngắn hoặc nội dung tiếng Việt (di sản bị ẩn thì 422; chưa cần xuất bản). Đang có lần dịch chạy (một dòng
   `ai_generations` đang `PROCESSING` của di sản, trong 10 phút) thì 409; dòng treo quá 10 phút coi như đã chết.
2. **Tóm tắt tiếng Việt** (một lần gọi) từ chữ của di sản; rồi **bốn lần dịch chạy song song** sang en, ko, zh-Hans, ja, mỗi lần dịch mọi phần chữ (tên, mô tả ngắn, nội dung, giai đoạn, sự kiện, chú thích ảnh)
   **kèm tóm tắt tiếng Việt vừa có**. Hàn, Trung, Nhật dịch từ bản tiếng Anh đã duyệt nếu có, không thì từ tiếng Việt.
3. Mỗi lần gọi một dòng `ai_generations`: `task_type` = `HERITAGE_SUMMARY` (tóm tắt) hoặc `HERITAGE_TRANSLATION` (mỗi ngôn ngữ), `heritage_id` có và `lesson_id` trống (CK `CK_ai_generations_subject`), `requested_by` là
   quản trị, `input_snapshot` (chữ đã gửi), `params`, `model`, `raw_output`, `status`, `error_message`, `completed_at`. Các dòng bản dịch và tóm tắt trỏ về lần gọi của mình qua `ai_generation_id`.
   **Không tính hạn mức lượt AI của trường** (`PlanGuard` chỉ đếm dòng có `lesson_id`); quản trị hệ thống không thuộc trường nào và không cần gói.
4. Mỗi lần gọi chờ tối đa **90 giây** (`Ai:HeritageAttemptTimeoutSeconds`), thử lại tối đa **2 lần** (`Ai:MaxRetries`) với lỗi mạng, quá thời gian, 5xx, 408, 429 và kết quả sai lược đồ; lỗi 4xx không thử lại.
   Kết quả được kiểm: tóm tắt phải có cả hai bản; bản dịch phải có tên; mã giai đoạn, sự kiện, ảnh lạ bị bỏ; chuỗi cắt về độ rộng cột; yêu cầu có tóm tắt mà thiếu là lỗi đầu ra (thử lại).
5. **Tất cả hoặc không gì cả**: một lần gọi hỏng sau hết lần thử thì các dòng nhật ký của lần đó ghi `FAILED` kèm lỗi, **không lưu bản dịch hay tóm tắt nào, bản cũ giữ nguyên**, trả **503** `AI_UNAVAILABLE`
   ("Dịch vụ AI đang bận. Bản dịch và tóm tắt hiện có được giữ nguyên …"). Tóm tắt tiếng Việt hỏng thì dừng, không gọi bốn lần dịch. Người gọi ngắt kết nối giữa chừng: các dòng nhật ký không kẹt ở `PROCESSING`.
6. Thành công thì ghi một giao dịch: bản dịch và tóm tắt **thay chính dòng cũ** (cập nhật tại chỗ, phần con không còn thì xóa), trạng thái `PENDING` (tiếng Anh, tóm tắt tiếng Việt và tiếng Anh) hoặc `MACHINE` (Hàn, Trung, Nhật),
   bỏ người và thời điểm duyệt, `is_outdated = 0`. Hai lần bấm đụng nhau ở ràng buộc UNIQUE thì lần thua báo 409.

## 10. Hợp đồng Dịch vụ AI

Dịch vụ AI thật (FastAPI) chưa có. Backend gọi qua giao diện `IHeritageAiClient`; chọn bản bằng `Ai:Mode` như soạn bài học: **`Simulated`** (mặc định, `SimulatedHeritageAiClient`, không gọi mạng, không dùng khóa nào) hoặc
**`Remote`** (`HttpHeritageAiClient`, `POST` tới `Ai:BaseUrl`, header `X-Internal-Key` = `Ai:InternalKey` và `X-Correlation-Id`, JSON UTF-8 camelCase). Dữ liệu gửi đi chỉ có chữ của di sản (không có họ tên, email, mã người dùng).

`POST /v1/summarize-heritage` — `{ generationId, heritageId, source: HeritageText }` → `{ shortText, detailedText, model, usage: { inputTokens, outputTokens } }`.

`POST /v1/translate-heritage` — `{ generationId, heritageId, targetLanguage, contentLanguage, content: HeritageText, summaryLanguage?, summary?: { shortText, detailedText } }` →
`{ content: HeritageText, summary?: { shortText, detailedText }, model, usage }`. Mã ngôn ngữ trong hợp đồng là `vi`, `en`, `ko`, `zh-Hans`, `ja`. Mã giai đoạn, sự kiện, ảnh được giữ nguyên để ghép lại.

`HeritageText` = `{ name, shortDescription?, content?, timelines: [{ id, name, description? }], events: [{ id, timelineId, name, description? }], media: [{ id, caption }] }`.

**Bản giả lập** (tất định, dùng để thử luồng, không phải chất lượng thật): tóm tắt ghép từ chữ đã có (bản ngắn ≤ 100 từ, bản chi tiết ≤ 350 từ, cắt ở ranh giới câu, không thêm dữ kiện); "dịch" gắn tiền tố mã ngôn ngữ đích vào từng phần chữ
(`[ko] Hoàng thành`), nên bản dịch máy được dịch lại từ tiếng Anh nhìn thấy `[ko] [en] …`. Nội dung thật dùng mô hình ngôn ngữ; quản trị hệ thống vẫn duyệt bản tiếng Việt và tiếng Anh.

## 11. Cấu hình

| Khóa | Mặc định | Ý nghĩa |
|---|---|---|
| `Ai:Mode` | `Simulated` | `Simulated` hoặc `Remote` (dùng chung với soạn bài học, sinh câu hỏi) |
| `Ai:BaseUrl`, `Ai:InternalKey` | trống | Chỉ cần khi `Remote`; khóa đặt bằng biến môi trường `Ai__InternalKey` hoặc User Secrets, không ghi vào appsettings |
| `Ai:HeritageAttemptTimeoutSeconds` | 90 | Thời gian chờ mỗi lần dịch hoặc tóm tắt di sản (soạn bài học vẫn 45 giây) |
| `Ai:MaxRetries` | 2 | Số lần thử lại thêm sau lần đầu (dùng chung) |

Không có khóa dịch vụ ngoài nào trong mã hay log: bản giả lập không dùng khóa, bản `Remote` chỉ dùng khóa nội bộ giữa hai dịch vụ của nhóm.

## 12. Kiểm thử

- Đơn vị (`LanguagesTests`, `HeritageAiClientTests`): đọc mã ngôn ngữ (dạng vùng, phồn thể bị loại), `Accept-Language`, ngôn ngữ mặc định, trạng thái ban đầu và điều kiện hiện; bản giả lập (độ dài tóm tắt, chỉ dùng chữ đã có,
  tất định, giữ mã phần con); bản HTTP (đường dẫn, khóa nội bộ, không lộ dữ liệu cá nhân, phân loại lỗi); bộ chạy (thử lại, thời gian chờ, kiểm và cắt kết quả).
- Tích hợp trên SQLite (`HeritageTranslationApiTests`, `HeritageLocalizationApiTests`): quyền chỉ quản trị hệ thống; dịch đủ 4 + 5 bản với trạng thái đúng và đủ phần con; nhật ký AI, không tính hạn mức trường; điều kiện dịch và
  chặn chạy song song; lỗi Dịch vụ AI (thử lại, không thử lại, quá thời gian, kết quả hỏng) giữ bản cũ; duyệt, từ chối (lý do vào nhật ký), sửa tại chỗ, hàng chờ, số đếm, bộ lọc; dịch lại thay dòng cũ; dịch lại bản máy từ bản
  tiếng Anh đã duyệt; cần dịch lại; xóa theo bản gốc; ngôn ngữ chọn bằng `lang` và `Accept-Language`, học sinh luôn tiếng Việt; hiện theo từng ngôn ngữ và lùi về tiếng Việt kèm cờ; tìm kiếm khớp bản dịch (kể cả ký tự đại diện);
  tóm tắt chỉ hiện sau khi tóm tắt tiếng Việt được duyệt; xem trước bản nháp theo ngôn ngữ; công khai không gọi AI.
- Tích hợp trên SQL Server LocalDB (`HeritageTranslationSqlServerTests`, database tạm tự xóa): toàn bộ luồng dịch, duyệt, hàng chờ, đọc, tìm, cần dịch lại, Cascade; CSDL tự từ chối dòng sai ngôn ngữ, sai trạng thái, trùng, sai khóa ngoại.
- Smoke trên API thật (Kestrel, LocalDB, bản giả lập AI): `scripts/smoke-da-ngon-ngu.sh` (78 bước); chạy trên database tạm, cách chạy ở đầu tệp.

## 13. Quyết định hiện thực cần biết

- **Một lần bấm gọi AI năm lần** (một tóm tắt, bốn bản dịch) thay vì một lần cho tất cả: mỗi lần có nhật ký, thử lại và thời gian chờ riêng, bốn lần dịch chạy song song. Bản dịch và tóm tắt của một ngôn ngữ
  cùng trỏ về lần gọi dịch ngôn ngữ đó (`ai_generation_id`); tóm tắt tiếng Việt trỏ về lần gọi tóm tắt.
- **Từ chối được cả bản đã duyệt** (không chỉ bản chờ duyệt) để quản trị gỡ được một bản đã hiện mà còn sai; đặc tả chỉ mô tả từ chối bản chờ duyệt.
- **Duyệt tiếng Anh dịch lại ngay** bản Hàn, Trung, Nhật trong lời gọi duyệt; đặc tả mô tả "ở lần bấm dịch kế tiếp" và "khi duyệt lại" nên làm cả hai.
- `address` của bản dịch để trống (AI không dịch địa chỉ), nên trang công khai luôn hiện địa chỉ gốc.
- Ghi lượt xem, đọc, nghe vào lịch sử của khách và gợi ý theo lịch sử **đã làm** ở luồng Khách tham quan ([khach-tham-quan.md](khach-tham-quan.md): `GET /summary` tự ghi lượt đọc khi có `X-Device-Key`, lượt nghe ghi qua `POST /api/v1/public/guest/activities`); cổng quảng cáo mở khóa tóm tắt thuộc luồng Quảng cáo, **chưa có** ở đây.

## 14. Thuyết minh tạo sẵn (giọng tự host)

Tab "Nghe" phát tệp MP3 tạo sẵn thay cho giọng của thiết bị khi có. Âm thanh tạo trên máy phát triển (tiếng Việt: VieNeu-TTS giọng "Mai Anh"; Anh, Hàn, Trung, Nhật: MeloTTS)
rồi tải lên qua API quản trị; **máy chủ không tạo giọng**. Thứ được đọc là `heritage_summaries.short_text`.

- Bảng `heritage_narrations` (migration `ThemThuyetMinhTaoSan`, chỉ thêm bảng): `heritage_id` (Restrict), `language_code` (vi, en, ko, zh, ja; CHECK như `heritage_summaries`), `text_hash` char(64)
  (SHA-256 hex thường của `short_text` đúng như đang lưu, chỉ máy chủ tính), `audio_url` (tệp MP3 trong kho tệp, thư mục `thuyet-minh/`, tên ngẫu nhiên; Cloudinary lưu loại raw như mô hình 3D), `duration_ms` (> 0),
  `sentences` nvarchar(max) JSON `[{ text, startMs, endMs }]`, `voice` (100), `created_at`, `updated_at`; UNIQUE (`heritage_id`, `language_code`). Tải lên lần nữa thay dòng cũ; tệp cũ vẫn nằm trong kho như mọi tệp bị thay.
- `GET /api/v1/admin/narrations/missing?language=vi&limit=500` (ADMIN): các tóm tắt **đang hiện công khai** ở ngôn ngữ đó (đúng quy tắc chọn của `GET /public/heritages/{slug}/summary`, cùng mã;
  bản lùi về tiếng Việt không tính) mà chưa có thuyết minh hoặc băm lệch. 200 `{ items: [{ heritageId, slug, name, language, shortText, textHash }], total }`; `language` của mục là mã ngắn (zh); `limit` 1..1000.
- `PUT /api/v1/admin/heritages/{heritageId}/narrations/{language}` (multipart: `audio` MP3 ≤ 10 MB kiểm bằng nội dung, `textHash`, `voice` 1..100, `durationMs` 1..1.800.000, `sentences` JSON 1..300 câu,
  `0 ≤ startMs < endMs ≤ durationMs + 1000`, `startMs` không giảm). 200 `{ heritageId, language, audioUrl, durationMs, voice, textHash, sentenceCount }`; 400; 404 (không có di sản hoặc tóm tắt ngôn ngữ đó);
  **409 `NARRATION_STALE`** khi băm khác băm của `short_text` hiện tại; 413 quá cỡ. Ghi nhật ký `HERITAGE_NARRATION_CHANGED` (đối tượng `heritage_narrations`, không ghi đường dẫn tệp).
- Công khai: `narration` chỉ có khi tồn tại thuyết minh cho **đúng ngôn ngữ của bản tóm tắt được trả** (lùi về tiếng Việt thì là bản tiếng Việt) và băm còn khớp `shortText`; không thì `null` và giao diện đọc
  bằng giọng thiết bị như cũ. Tệp bản Local phục vụ tại `/files/thuyet-minh/{mã}.mp3` (`audio/mpeg`).
- Kiểm thử: `NarrationRulesTests`, `NarrationServiceTests` (SQLite, kho tệp giả), `NarrationApiTests` (HTTP), `NarrationSqlServerTests` (LocalDB, ràng buộc CSDL).
