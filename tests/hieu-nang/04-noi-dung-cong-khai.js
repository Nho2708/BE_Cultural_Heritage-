// Đọc nội dung công khai: 100 người dùng đồng thời, xoay vòng 5 ngôn ngữ (vi, en, ko, zh-Hans, ja).
// Chỉ GET công khai, không đăng nhập, không ghi, không đụng cổng quảng cáo (tóm tắt và nghe thuyết minh bị cổng đó chặn nên không đo ở đây).
// Ngưỡng: thời gian trả lời trung bình <= 800 ms, P95 <= 1,5 giây, không lỗi máy chủ.
//
// QUAN TRỌNG: API công khai giới hạn 300 yêu cầu mỗi phút mỗi IP. 100 người dùng từ một máy sẽ vượt xa mức đó và k6 sẽ đo toàn 429.
// Phải nới giới hạn trên máy thử (RateLimiting__PublicRequestsPerMinute) hoặc chạy từ nhiều IP; xem README.
// Lượt chạy nhẹ: NHE=1 (1 người dùng, 5 lượt lặp) dùng được với https://diviet.id.vn.
import { sleep, check } from 'k6';
import exec from 'k6/execution';
import { goi, duLieuJson, NGON_NGU, kichBanNhe } from './chung.js';

const THOI_GIAN = __ENV.THOI_GIAN || '2m';

export const options = {
  scenarios: {
    doc_cong_khai: __ENV.NHE
      ? Object.assign(kichBanNhe(), { exec: 'docNoiDung' })
      : { executor: 'constant-vus', vus: 100, duration: THOI_GIAN, exec: 'docNoiDung' },
  },
  thresholds: {
    http_req_duration: ['avg<800', 'p(95)<1500'],
    loi_may_chu: ['count==0'],
    bi_gioi_han_tan_suat: ['count==0'],
  },
};

// Lấy danh sách đường dẫn (slug) di sản đã xuất bản một lần cho cả cuộc chạy.
export function setup() {
  const res = goi('GET', '/api/v1/public/heritages?page=1&pageSize=100', 'chuan_bi', { chuanBi: true });
  const d = duLieuJson(res);
  const slugs = d && d.items ? d.items.map((h) => h.slug) : [];
  if (slugs.length === 0) exec.test.abort('Không lấy được danh sách di sản công khai (HTTP ' + res.status + ')');
  return { slugs: slugs };
}

export function docNoiDung(data) {
  const lang = NGON_NGU[(exec.vu.idInTest + exec.scenario.iterationInInstance) % NGON_NGU.length];
  const slug = data.slugs[Math.floor(Math.random() * data.slugs.length)];
  const q = '?lang=' + encodeURIComponent(lang);

  check(goi('GET', '/api/v1/public/home' + q, 'trang_chu'), { 'trang chu 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);
  check(goi('GET', '/api/v1/public/heritages' + q + '&page=1&pageSize=20', 'danh_sach_di_san'), { 'danh sach 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);
  check(goi('GET', '/api/v1/public/heritages/' + encodeURIComponent(slug) + q, 'chi_tiet_di_san'), { 'chi tiet 200': (r) => r.status === 200 });
  sleep(1 + Math.random() * 2);
  goi('GET', '/api/v1/public/heritages/' + encodeURIComponent(slug) + '/timeline' + q, 'dong_thoi_gian');
  sleep(1 + Math.random() * 2);
}
