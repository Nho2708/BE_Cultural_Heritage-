// Khách đồng thời: 200 khách, mỗi khách gửi 1 yêu cầu mỗi 3 giây, kéo dài 5 phút (khoảng 67 yêu cầu mỗi giây).
// Mỗi khách có một mã thiết bị riêng (tiêu đề X-Device-Key). Tỉ lệ: 4 yêu cầu đọc rồi 1 yêu cầu ghi lịch sử (từ khóa tìm kiếm khác nhau để lượt ghi thật sự).
// Ngưỡng: đọc P95 <= 1,5 giây; ghi lịch sử P95 <= 300 ms; không lỗi máy chủ.
//
// QUAN TRỌNG: 200 khách từ một máy là khoảng 4.000 yêu cầu mỗi phút từ một IP, vượt xa giới hạn 300 yêu cầu mỗi phút mỗi IP của API công khai
// (và mỗi mã thiết bị còn có giới hạn riêng: 120 lượt ghi lịch sử mỗi phút). Chưa nới RateLimiting__PublicRequestsPerMinute (đặt từ 10000 trở lên)
// hoặc chưa chạy từ nhiều IP thì kết quả là 429, bộ đếm bi_gioi_han_tan_suat sẽ làm kịch bản không đạt. Xem README.
import { sleep, check } from 'k6';
import exec from 'k6/execution';
import { goi, duLieuJson, NGON_NGU, uuidV4, kichBanNhe } from './chung.js';

const THOI_GIAN = __ENV.THOI_GIAN || '5m';
const NHIP_GIAY = 3;

export const options = {
  scenarios: {
    khach: __ENV.NHE
      ? Object.assign(kichBanNhe(), { exec: 'motKhach' })
      : { executor: 'constant-vus', vus: 200, duration: THOI_GIAN, exec: 'motKhach' },
  },
  thresholds: {
    'http_req_duration{loai:doc}': ['p(95)<1500'],
    'http_req_duration{loai:ghi}': ['p(95)<300'],
    loi_may_chu: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
  },
};

export function setup() {
  const res = goi('GET', '/api/v1/public/heritages?page=1&pageSize=100', 'chuan_bi', { chuanBi: true });
  const d = duLieuJson(res);
  const slugs = d && d.items ? d.items.map((h) => h.slug) : [];
  if (slugs.length === 0) exec.test.abort('Không lấy được danh sách di sản công khai (HTTP ' + res.status + ')');
  return { slugs: slugs };
}

// Trạng thái riêng của từng khách ảo.
let khach = null;

export function motKhach(data) {
  if (khach === null) {
    khach = { thietBi: uuidV4(), lan: 0, ngonNgu: NGON_NGU[exec.vu.idInTest % NGON_NGU.length] };
    sleep(Math.random() * NHIP_GIAY); // rải đều giờ vào
    goi('POST', '/api/v1/public/guest/devices', 'chuan_bi', { thietBi: khach.thietBi, chuanBi: true }, { language: khach.ngonNgu });
  }
  khach.lan += 1;
  const lang = '?lang=' + encodeURIComponent(khach.ngonNgu);
  const slug = data.slugs[Math.floor(Math.random() * data.slugs.length)];
  const luot = khach.lan % 5;

  if (luot === 0) {
    const tuKhoa = 'thang long ' + exec.vu.idInTest + ' ' + khach.lan;
    const res = goi('POST', '/api/v1/public/guest/activities' + lang, 'ghi_lich_su', { thietBi: khach.thietBi, loai: 'ghi' },
      { type: 'SEARCH', query: tuKhoa });
    check(res, { 'ghi lich su 200': (r) => r.status === 200 });
  } else if (luot === 1) {
    check(goi('GET', '/api/v1/public/home' + lang, 'doc_trang_chu', { thietBi: khach.thietBi, loai: 'doc' }), { 'doc 200': (r) => r.status === 200 });
  } else if (luot === 2) {
    check(goi('GET', '/api/v1/public/heritages' + lang + '&page=1&pageSize=20', 'doc_danh_sach', { thietBi: khach.thietBi, loai: 'doc' }),
      { 'doc 200': (r) => r.status === 200 });
  } else if (luot === 3) {
    check(goi('GET', '/api/v1/public/heritages/' + encodeURIComponent(slug) + lang, 'doc_chi_tiet', { thietBi: khach.thietBi, loai: 'doc' }),
      { 'doc 200': (r) => r.status === 200 });
  } else {
    check(goi('GET', '/api/v1/public/guest/history' + lang, 'doc_lich_su', { thietBi: khach.thietBi, loai: 'doc' }),
      { 'doc 200': (r) => r.status === 200 });
  }
  sleep(NHIP_GIAY);
}
