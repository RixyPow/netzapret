-- Наш модуль для winws2. К движку его кладут build.cmd и pack.cmd под именем
-- lua/nz-video.lua, пресет подключает строкой --lua-init=@lua/nz-video.lua
-- (Universal V13 YT FIX).
--
-- Первый пакет ответа сервера — не больше двух байт, дальше окно настоящее.
--
-- Фильтр по адресу на зарубежных узлах видео Google (ТСПУ, с октября 2026) режет соединение
-- по первому пакету ответа сервера: начинается он с 16 03 03 (запись TLS) — поток гибнет,
-- какое бы имя ни стояло в приветствии. Смотрит он только этот первый пакет. Замер 10.11 на
-- трёх сетях: окно приёма 2 Б — ответ проходит (3 из 3), 3 Б и больше — нет (0 из 9); после
-- первых двух байт окно можно открыть, и весь ответ сервера (4,6 КБ) приходит за 0,13 с.
--
-- Окно в два байта множителем не выразить: множитель окна задаётся в SYN один раз на всё
-- соединение, у Windows он 8, и наименьшее ненулевое окно — 256 Б. Занизить множитель в SYN
-- (как wssize из zapret-antidpi) — значит до конца соединения показывать серверу окно
-- в 2^(8-заниженный) раз меньше настоящего: скорость падает в десятки раз. Поэтому множитель
-- из SYN убирается вовсе: сервер тогда свой тоже не шлёт, Windows выключает масштаб у себя,
-- и обе стороны считают окно одинаково — до 65535 Б, без подгонки каждого подтверждения
-- (её делает wssize_ramp из custom_funcs, но ей нужны все пустые подтверждения, а их
-- WinDivert отдаёт только с --wf-tcp-empty=1).
--
-- Цена — те самые 65535 Б: замер 10.11 дома, видео через зарубежный узел — 6,7–7,7 Мбит/с
-- вместо 150–300. А режет фильтр не всех: дома — пятую часть соединений (132 рукопожатия
-- из 150 проходят и так), на других сетях часть узлов наглухо, а остальные не трогает.
-- Поэтому малое окно получает не всякое соединение, а только группа узлов, у которой ответ
-- только что срезали: приветствие ушло, а ответа за две секунды не было. Срез — малое окно
-- на минуту: узел, потерявший одно соединение, через минуту снова работает на полной
-- скорости. На час — группе, закрытой наглухо: второй срез подряд, если обычным соединением
-- она не отвечала ни разу, третий подряд — если отвечала. Одного «второго подряд» мало:
-- дома первое обычное соединение после минуты срезается так же часто, как любое другое
-- (6 из 47 вечером 10.11), и группа за группой уходили в час за пять минут пробы.
--
-- Счёт ведётся не по адресу, а по группе узлов — 16 соседних адресов: узлы одной группы
-- (rr1…rr5 стоят подряд) фильтр закрывает вместе, и плееру, перешедшему на соседний, не
-- приходится виснуть на каждом. Шире нельзя: на сети с закрытыми узлами 173.194.164.241
-- был закрыт, а 173.194.164.209 из той же /24 отвечал (замер 10.11). И не 32, как было
-- до 11.11: группа серверов Google видна по обратной записи DNS (…-in-fN.1e100.net),
-- и у узлов вида sn-aj5go5-5p она — 16 адресов; 32 склеивали две площадки в разных
-- городах (74.125.111.96–111 и .112–127: задержка 88 и 45 мс), и ответы живой площадки
-- обнуляли бы счёт срезов закрытой. У крупных площадок группа — 32 адреса, их половины
-- учатся врозь; пять адресов одного узла в одну шестнадцатку попадают всегда.
--
-- Срезанный ответ узнаётся на следующем SYN к той же группе (так помечается группа), а на
-- самом зависшем соединении — по таймеру через resetafter с (arg reset): оно без ответа
-- пакетов больше не шлёт, ждать его клиент будет до своего таймаута, поэтому с reset ему
-- сразу шлётся RST и плеер повторяет; без reset первое такое соединение виснет, зато
-- следующее к группе проходит. Проверено 11.11 замером A/B: без reset срезанное молчит до
-- таймаута (5,3 с у пробы, в браузере дольше), с reset — сброс за ~2 с при пороге 1,6 с
-- и за 1,36–1,50 с при пороге 1 с, дальше малое окно. Порог по умолчанию — 1 с: на 101
-- здоровом соединении ложных сбросов при нём не было (все ответили быстрее секунды).
-- Приветствия без ответа помнятся по
-- соединениям, а не одно на группу: плеер открывает к узлу несколько соединений сразу
-- (видео и проверка связи), и ответ одного не должен стирать молчание другого — замер
-- 10.11: при двух адресах группы одновременно из пяти срезов два терялись. И ответ
-- засчитывается, только если пришёл в срок: запоздалый (сервер повторяет ответ и после
-- того, как клиент сдался) иначе обнулял бы счёт срезов и делал группу «отвечавшей».
--
-- Два шага, потому что у SYN имени ещё нет и его берёт секция по адресам, а приветствие —
-- секция по имени:
--   nz_first2:syn  — в секции по адресам: решает, нужно ли адресу малое окно, и если да —
--                    убирает множитель окна из SYN;
--   nz_first2      — в секциях по имени (googlevideo.com и youtube.com: те же узлы плеер
--                    зовёт и как rrN---sn-….c.youtube.com), перед рецептом приветствия: соединению с малым
--                    окном ставит исходящим пакетам окно в два байта, пока сервер не прислал
--                    ни байта; за обычным следит — пришёл ли ответ. Соединения, чей SYN
--                    первый шаг не видел (узел провайдера), не трогает.
-- Подтверждение первых двух байт уходит от Windows уже с настоящим окном — пустые пакеты
-- сюда не попадают, — и сервер шлёт остальное как обычно.
--
-- standard args : direction
-- arg : syn - шаг для SYN (см. выше)
-- arg : always - (с syn) малое окно всем соединениям, без оглядки на то, резали ли ответ
-- arg : after - сколько секунд приветствие может ждать ответа (по умолчанию 2); в обоих шагах
--               одно и то же: шаг по имени позже этого срока ответ уже не засчитывает
-- arg : hold - (с syn) на сколько секунд группа получает малое окно после среза (60)
-- arg : hold2 - (с syn) на сколько — группа, закрытая наглухо (3600)
-- arg : wsize - окно до первых байт ответа сервера (по умолчанию 2)
-- arg : reset - (в шаге по имени) первое соединение, чей ответ срезан, не ждёт таймаута
--               клиента: через resetafter с без ответа ему шлётся RST от имени сервера
--               (dis_reverse + ifin), плеер сразу открывает новое — и оно идёт с малым
--               окном, потому что группа к этому мигу помечена. Без reset первое висит.
-- arg : resetafter - (с reset) через сколько секунд слать RST (по умолчанию 1; обычный
--                     ответ узла — меньше 0.5 с, самый долгий в замере — 0,97 с)

-- Группа узлов → до какого времени ей малое окно.
local nz_first2_until = {}
-- Группа узлов → приветствия обычных соединений, на которые ещё нет ответа:
-- номер соединения → когда ушло.
local nz_first2_asked = {}
-- Номер, выданный последнему обычному соединению с приветствием.
local nz_first2_serial = 0
-- Группа узлов → сколько раз подряд ответ не пришёл.
local nz_first2_cuts = {}
-- Группа узлов → обычному соединению она хоть раз ответила: значит, не закрыта наглухо.
local nz_first2_alive = {}

-- Группа узла: у IPv4 — 16 соседних адресов (последний байт без четырёх младших бит),
-- у IPv6 — сам адрес.
local function nz_first2_group(ip)
	if #ip ~= 4 then return ip end
	return string.sub(ip, 1, 3)..string.char(bitand(string.byte(ip, 4), 240))
end

-- Быстрый отказ (arg reset, с 11.11): сброс зависшего первого соединения к клиенту, чтобы
-- плеер повторил сразу, не дожидаясь своего таймаута. По таймеру через resetafter с после
-- приветствия без ответа: группа помечается (повтор пойдёт с малым окном) и клиенту от имени
-- шлётся RST (dis_reverse + ifin, как stock standard_failure_detector). Только с arg reset.
function nz_first2_reset_fire(name, data)
	local st = data.st
	if st.nz_first2_data_seen then return end
	if st.nz_first2 ~= "watch" then return end
	local ip = data.ip
	local wait = nz_first2_asked[ip]
	if wait and data.id then
		wait[data.id] = nil
		if next(wait) == nil then nz_first2_asked[ip] = nil end
	end
	local cuts = (nz_first2_cuts[ip] or 0) + 1
	nz_first2_cuts[ip] = cuts
	local closed = cuts >= (nz_first2_alive[ip] and 3 or 2)
	nz_first2_until[ip] = os.time() + (closed and data.hold2 or data.hold)
	rawsend_dissect(data.dis, {ifout = data.ifin})
	if b_debug then DLOG("nz_first2: сброс зависшего соединения к клиенту; группа помечена, срез "..cuts) end
end

function nz_first2(ctx, desync)
	local dis = desync.dis
	if not dis.tcp then
		if not dis.icmp then instance_cutoff_shim(ctx, desync) end
		return
	end
	direction_cutoff_opposite(ctx, desync)
	if not direction_check(desync) then return end
	local track = desync.track
	local ip = dis_ipdst(dis)
	if ip then ip = nz_first2_group(ip) end
	if not track or not ip then
		-- Без учёта соединения не узнать ни пометки, ни того, начал ли сервер отвечать.
		instance_cutoff_shim(ctx, desync)
		return
	end
	local tcp = dis.tcp
	local st = track.lua_state
	if desync.arg.syn then
		if bitand(tcp.th_flags, TH_SYN) == 0 then
			instance_cutoff_shim(ctx, desync)
			return
		end
		local small = desync.arg.always ~= nil
		if not small then
			local now = os.time()
			local wait = nz_first2_asked[ip]
			if wait then
				-- Приветствия к этой группе, оставшиеся без ответа дольше срока: ответ срезан.
				-- Сколько бы их ни набралось к этому SYN, срез считается один.
				local after = tonumber(desync.arg.after) or 2
				local cut = false
				for id, at in pairs(wait) do
					if now - at >= after then
						wait[id] = nil
						cut = true
					end
				end
				if next(wait) == nil then nz_first2_asked[ip] = nil end
				if cut then
					local cuts = (nz_first2_cuts[ip] or 0) + 1
					nz_first2_cuts[ip] = cuts
					local closed = cuts >= (nz_first2_alive[ip] and 3 or 2)
					local hold = closed and (tonumber(desync.arg.hold2) or 3600) or (tonumber(desync.arg.hold) or 60)
					nz_first2_until[ip] = now + hold
					if b_debug then DLOG("nz_first2: группа "..(#ip == 4 and ntop(ip) or "ipv6").." ответ срезан "..cuts.." раз — малое окно на "..hold.." с") end
				end
			end
			local till = nz_first2_until[ip]
			if till then
				if now < till then
					small = true
				else
					nz_first2_until[ip] = nil
				end
			end
		end
		if not small then
			st.nz_first2 = "watch"
			return
		end
		st.nz_first2 = "small"
		local i = find_tcp_option(tcp.options, TCP_KIND_SCALE)
		if not i then return end
		table.remove(tcp.options, i)
		if b_debug then DLOG("nz_first2: SYN без множителя окна") end
		return VERDICT_MODIFY
	end
	local mode = st.nz_first2
	if not mode then
		instance_cutoff_shim(ctx, desync)
		return
	end
	local server = track.pos and track.pos.server and track.pos.server.tcp
	local acked = 0
	-- Номер подтверждения значим только с флагом ACK: у сброса без него в поле ноль.
	if server and server.seq0 and bitand(tcp.th_flags, TH_ACK) ~= 0 then
		acked = (tcp.th_ack - server.seq0 - 1) % 4294967296
		if acked >= 2147483648 then acked = 0 end
	end
	if acked > 0 then
		if mode == "watch" then
			st.nz_first2_data_seen = true
			-- Обычному соединению сервер ответил в срок: группа не закрыта, счёт начинается
			-- заново. Забывается только своё приветствие — чужое молчание остаётся на счету.
			-- Запоздалый ответ не меняет ничего: приветствие остаётся на счету до следующего
			-- SYN или уже сочтено срезом. Замер 10.11: группа, у которой первое же соединение
			-- срезал фильтр, сочлась отвечавшей, а счёт срезов обнулился — засчитаться там мог
			-- только запоздалый ответ того соединения (сервер повторяет его и после того, как
			-- клиент сдался). Самого запоздалого пакета я не видел, это вывод из хода счёта.
			local wait = nz_first2_asked[ip]
			local id = st.nz_first2_id
			local at = wait and id and wait[id]
			if at and os.time() - at <= (tonumber(desync.arg.after) or 2) then
				wait[id] = nil
				if next(wait) == nil then nz_first2_asked[ip] = nil end
				nz_first2_cuts[ip] = nil
				nz_first2_alive[ip] = true
			end
		end
		instance_cutoff_shim(ctx, desync)
		return
	end
	if #dis.payload == 0 then return end
	if mode == "watch" then
		-- Приветствие обычного соединения: запомнить, когда ушло (повторы времени не сдвигают).
		if not st.nz_first2_id then
			nz_first2_serial = nz_first2_serial + 1
			st.nz_first2_id = nz_first2_serial
			local wait = nz_first2_asked[ip]
			if not wait then
				wait = {}
				nz_first2_asked[ip] = wait
			end
			wait[st.nz_first2_id] = os.time()
		end
		if desync.arg.reset and not st.nz_first2_armed then
			st.nz_first2_armed = true
			local rst = deepcopy(dis)
			rst.payload = nil
			dis_reverse(rst)
			rst.tcp.th_flags = TH_RST
			rst.tcp.th_win = (track.pos and track.pos.reverse and track.pos.reverse.tcp and track.pos.reverse.tcp.winsize) or 64
			rst.tcp.options = nil
			local ms = math.floor((tonumber(desync.arg.resetafter) or 1) * 1000)
			timer_set("nzr_"..desync_timer_name(desync), "nz_first2_reset_fire", ms, true, {
				dis = rst, ifin = desync.ifin, st = st, ip = ip, id = st.nz_first2_id,
				hold = tonumber(desync.arg.hold) or 60, hold2 = tonumber(desync.arg.hold2) or 3600,
			})
		end
		return
	end
	tcp.th_win = tonumber(desync.arg.wsize) or 2
	if b_debug then DLOG("nz_first2: окно "..tcp.th_win.." до первых байт ответа") end
	return VERDICT_MODIFY
end
