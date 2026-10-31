# Sơ đồ dạng mã nguồn

Sơ đồ thiết kế của DiViet lưu bằng PlantUML và DBML để xem lại được theo từng phiên bản code.

| Tệp | Nội dung |
|---|---|
| `dsvhvn.dbml` | Mô hình dữ liệu: 43 bảng, 69 khóa ngoại, 10 nhóm bảng, khớp snapshot migration |
| `erd-tong-quat.puml` | ERD tổng quát, chỉ có cột khóa |
| `erd-chi-tiet.puml` | ERD đủ mọi cột |
| `kien-truc-he-thong.puml` | Kiến trúc tổng thể |
| `trien-khai-he-thong.puml` | Triển khai trên Google Cloud, CI và CD |
| `hoat-dong-*.puml` | 6 luồng nghiệp vụ chính, mỗi tác nhân một làn |
| `trang-thai-*.puml` | Vòng đời của 15 thực thể có trạng thái |

Các tệp ở thư mục này là bản tiếng Anh, dùng trong report. Thư mục `vi/` là bản tiếng Việt có cùng nội dung.

Xem hoặc xuất ảnh bằng PlantUML:

```
java -jar plantuml.jar -tpng docs/so-do/kien-truc-he-thong.puml
```

Mô hình trong `dsvhvn.dbml` dán được vào https://dbdiagram.io để xem hình.
