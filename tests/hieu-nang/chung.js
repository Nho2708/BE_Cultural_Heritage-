// Phần dùng chung của các kịch bản k6: địa chỉ gốc, nạp dữ liệu, gọi API, bộ đếm lỗi.
// Địa chỉ gốc lấy từ biến môi trường BASE_URL (mặc định là máy cục bộ để không lỡ tay đo lên máy chủ thật).
import http from 'k6/http';
import exec from 'k6/execution';
import { sleep } from 'k6';
import { Counter } from 'k6/metrics';
import { SharedArray } from 'k6/data';

export const BASE_URL = (__ENV.BASE_URL || 'http://localhost:5180').replace(/\/+$/, '');
export const NGON_NGU = ['vi', 'en', 'ko', 'zh-Hans', 'ja'];

// Bộ đếm dùng cho ngưỡng "không lỗi máy chủ", "không bị chặn nhầm", "không bị giới hạn tần suất".
export const loiMayChu = new Counter('loi_may_chu'); // trả lời 5xx
export const biGioiHan = new Counter('bi_gioi_han_tan_suat'); // trả lời 429: đo nhầm giới hạn tần suất, không phải hiệu năng
export const hocSinhBiChanNham = new Counter('hoc_sinh_bi_chan_nham'); // em có tài khoản đúng mà đăng nhập bị 401, 403 hoặc 429
export const luuThatBai = new Counter('luu_that_bai'); // lưu câu trả lời không được 200
export const baiMat = new Counter('bai_mat'); // nộp bài không được 200 hoặc không xem lại được kết quả

// Dữ liệu ngoài git: mặc định du-lieu/du-lieu.json (đường dẫn tính từ thư mục chứa kịch bản), đổi bằng biến DU_LIEU.
// Thiếu tệp thì không làm hỏng giai đoạn khởi tạo (để k6 inspect chạy được); kịch bản dừng ngay ở bước chuẩn bị.
const duongDan = __ENV.DU_LIEU || './du-lieu/du-lieu.json';
const kho = new SharedArray('du-lieu', function () {
  try {
    return [JSON.parse(open(duongDan))];
  } catch (e) {
    return [{ loi: 'Không đọc được ' + duongDan + ': ' + e }];
  }
});

export function layDuLieu() {
  const d = kho[0];
  if (d.loi) exec.test.abort(d.loi + ' (xem README.md, mục Chuẩn bị dữ liệu)');
  if (!d.hocSinh || d.hocSinh.length === 0) exec.test.abort('du-lieu.json không có học sinh nào');
  return d;
}

// Lượt chạy nhẹ để thử cú pháp: NHE=1 thì 1 người dùng, 5 lượt lặp.
export function kichBanNhe() {
  return { executor: 'per-vu-iterations', vus: 1, iterations: 5, maxDuration: '2m' };
}

export function uuidV4() {
  const h = '0123456789abcdef';
  let s = '';
  for (let i = 0; i < 36; i++) {
    if (i === 8 || i === 13 || i === 18 || i === 23) s += '-';
    else if (i === 14) s += '4';
    else if (i === 19) s += '89ab'[Math.floor(Math.random() * 4)];
    else s += h[Math.floor(Math.random() * 16)];
  }
  return s;
}

function ghiNhan(res) {
  if (res.status >= 500) loiMayChu.add(1);
  if (res.status === 429) biGioiHan.add(1);
}

// Gọi API. ten: nhãn dùng cho ngưỡng (tag name). tuyChon: { token, thietBi, chuanBi, loai }.
// chuanBi = true thì gắn nhãn chuan_bi để không lẫn vào số đo chính.
export function goi(phuongThuc, duong, ten, tuyChon, body) {
  const t = tuyChon || {};
  const headers = { Accept: 'application/json' };
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  if (t.token) headers.Authorization = 'Bearer ' + t.token;
  if (t.thietBi) headers['X-Device-Key'] = t.thietBi;
  const res = http.request(phuongThuc, BASE_URL + duong, body === undefined ? null : JSON.stringify(body), {
    headers: headers,
    tags: t.loai ? { name: t.chuanBi ? 'chuan_bi' : ten, loai: t.loai } : { name: t.chuanBi ? 'chuan_bi' : ten },
  });
  ghiNhan(res);
  return res;
}

export function duLieuJson(res) {
  try {
    const j = res.json();
    return j ? j.data : null;
  } catch (e) {
    return null;
  }
}

// Đăng nhập học sinh. Trả về token hoặc null. hocSinhBiChanNham tăng khi tài khoản đúng mà bị từ chối.
export function dangNhapHocSinh(hs, tuyChon) {
  const t = tuyChon || {};
  const res = goi('POST', '/api/v1/student/auth/login', 'dang_nhap', t, { studentCode: hs.maHocSinh, password: hs.matKhau });
  if (res.status === 200) {
    const d = duLieuJson(res);
    return d && d.accessToken ? d.accessToken : null;
  }
  if (res.status === 401 || res.status === 403 || res.status === 429) hocSinhBiChanNham.add(1);
  return null;
}

// Mở trang học của em (trang chính sau khi đăng nhập).
export function trangHoc(token, tuyChon) {
  return goi('GET', '/api/v1/student/home', 'trang_hoc', Object.assign({ token: token }, tuyChon || {}));
}

// Bắt đầu (hoặc làm tiếp) lượt làm bài. Trả về dữ liệu lượt hoặc null.
export function batDauLuot(token, baiKiemTraId, tuyChon) {
  const res = goi('POST', '/api/v1/student/quizzes/' + baiKiemTraId + '/attempts', 'bat_dau_luot', Object.assign({ token: token }, tuyChon || {}));
  return res.status === 200 || res.status === 201 ? duLieuJson(res) : null;
}

// Câu trả lời hợp lệ cho dạng chọn một phương án (bài tải chỉ gồm dạng này): chọn ngẫu nhiên một phương án.
export function traLoiChon(cau) {
  const n = cau.options && cau.options.length ? cau.options.length : 1;
  return { selectedIndex: Math.floor(Math.random() * n) };
}

export function luuCau(token, luotId, cau, ten, tuyChon) {
  return goi('PUT', '/api/v1/student/attempts/' + luotId + '/answers/' + cau.questionId, ten, Object.assign({ token: token }, tuyChon || {}), traLoiChon(cau));
}

// Học sinh thứ i (vòng lại khi hết danh sách).
export function hocSinhThu(d, i) {
  return d.hocSinh[i % d.hocSinh.length];
}

// Chờ tới mốc thời gian (mili giây từ lúc bắt đầu chạy) để mọi người dùng ảo cùng nộp bài trong một khung giờ.
export function choDenMoc(mocMs) {
  const con = mocMs - exec.instance.currentTestRunDuration;
  if (con > 0) sleep(con / 1000);
}
