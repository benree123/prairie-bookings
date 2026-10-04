"""Staff integration checks. Run only against a disposable demo database."""
import os,json,urllib.request,urllib.error,http.cookiejar,datetime,sys,concurrent.futures
base=os.getenv('PRAIRIE_TEST_URL','http://localhost:5081')
password=os.environ['PRAIRIE_TEST_PASSWORD']
cookies=http.cookiejar.CookieJar()
staff=urllib.request.build_opener(urllib.request.HTTPCookieProcessor(cookies))
anonymous=urllib.request.build_opener()
passed=0
refs=[]
blocks=[]
def call(path,body=None,auth=False,header=True):
    headers={}
    if body is not None:
        headers['Content-Type']='application/json'
        if header: headers['X-Staff-Request']='1'
    request=urllib.request.Request(base+path,data=None if body is None else json.dumps(body).encode(),headers=headers)
    try: response=(staff if auth else anonymous).open(request,timeout=15)
    except urllib.error.HTTPError as error: response=error
    raw=response.read()
    return response.code,json.loads(raw) if raw else {},response.headers

def check(condition,name):
    global passed
    if not condition: raise AssertionError(name)
    passed+=1
    print('PASS:',name)
try:
    catalog=call('/api/catalog')[1]
    day=datetime.date.fromisoformat(catalog['today'])+datetime.timedelta(days=2)
    while day.weekday()>=5: day+=datetime.timedelta(days=1)
    date=day.isoformat();start=date+'T10:00:00'
    query=f'/api/staff/appointments?from={date}&to={date}'
    check(call(query)[0]==401,'Appointment data requires authentication')
    check(call('/api/staff/blocks?providerId=3&date='+date)[0]==401,'Availability management requires authentication')
    check(call('/api/staff/appointments/1/cancel',{},header=True)[0]==401,'Anonymous users cannot cancel by appointment ID')
    login={'username':'staff','password':password}
    check(call('/api/staff/login',login,auth=True,header=False)[0]==400,'Login rejects requests without CSRF header')
    check(call('/api/staff/login',{'username':'staff','password':'wrong'},auth=True)[0]==401,'Incorrect password is rejected')
    code,data,headers=call('/api/staff/login',login,auth=True)
    check(code==200 and data['username']=='staff','Valid staff login succeeds')
    cookie=headers.get('Set-Cookie','').lower()
    check('httponly' in cookie and 'samesite=strict' in cookie,'Session cookie is HttpOnly and SameSite Strict')
    check(call('/api/staff/session',auth=True)[0]==200,'Staff session persists between requests')
    check(call('/api/staff/appointments?from=invalid&to='+date,auth=True)[0]==400,'Invalid date filters are rejected')
    check(call(query+'&providerId=999',auth=True)[0]==400,'Unknown advisor filters are rejected')
    check(call('/api/staff/blocks',{'providerId':3,'startsAt':start,'reason':'Test meeting'},auth=True,header=False)[0]==400,'Authenticated mutations require CSRF header')
    code,block,_=call('/api/staff/blocks',{'providerId':3,'startsAt':start,'reason':'Test meeting'},auth=True)
    if code==201: blocks.append(block['id'])
    check(code==201,'Staff can block a future slot')
    slots=call('/api/availability?providerId=3&date='+date)[1]['slots']
    check(not next(x for x in slots if x['startsAt']==start)['available'],'Blocked time disappears from customer availability')
    body={'providerId':3,'serviceId':1,'startsAt':start,'customerName':'Staff Integration Test','email':'staff-test@example.com'}
    check(call('/api/bookings',body)[0]==409,'Customers cannot book a staff-blocked time')
    code,data,_=call(query,auth=True)
    check(code==200 and data['counts']['total']==0,'Staff blocks are excluded from appointment counts')
    check(call('/api/staff/blocks/'+str(block['id'])+'/release',{},auth=True)[0]==200,'Removing a block frees the reservation')
    code,booking,_=call('/api/bookings',body)
    if code==201: refs.append(booking['reference'])
    check(code==201,'A released slot can be booked by a customer')
    check(call('/api/staff/blocks',{'providerId':3,'startsAt':start,'reason':'Test meeting'},auth=True)[0]==409,'Staff cannot block an existing customer appointment')
    data=call(query+'&providerId=3&status=active',auth=True)[1]
    check(len(data['appointments'])==1 and data['counts']['upcoming']==1,'Date, advisor, and status filters return the matching appointment')
    appointment=data['appointments'][0]
    check('reference' not in appointment and 'referenceHash' not in appointment,'Staff API does not expose cancellation secrets')
    check(call('/api/staff/appointments/'+str(appointment['id'])+'/cancel',{},auth=True)[0]==200,'Staff can cancel a future customer appointment')
    cancelled=call(query+'&status=cancelled',auth=True)[1]
    check(len(cancelled['appointments'])==1 and cancelled['counts']['cancelled']==1,'Cancellation updates counts and status filters')
    check(next(x for x in call('/api/availability?providerId=3&date='+date)[1]['slots'] if x['startsAt']==start)['available'],'Staff cancellation releases public availability')
    with concurrent.futures.ThreadPoolExecutor(max_workers=2) as pool:
        block_task=pool.submit(call,'/api/staff/blocks',{'providerId':3,'startsAt':start,'reason':'Concurrent test'},True)
        booking_task=pool.submit(call,'/api/bookings',body)
        block_result=block_task.result();booking_result=booking_task.result()
    if block_result[0]==201: blocks.append(block_result[1]['id'])
    if booking_result[0]==201: refs.append(booking_result[1]['reference'])
    check(sorted([block_result[0],booking_result[0]])==[201,409],'Concurrent staff block and customer booking cannot overlap')
    check(call('/api/staff/logout',{},auth=True)[0]==200 and call(query,auth=True)[0]==401,'Logout removes access to protected appointment data')
    print(f'{passed} staff checks passed.')
finally:
    try:
        call('/api/staff/login',{'username':'staff','password':password},auth=True)
        for block_id in blocks: call('/api/staff/blocks/'+str(block_id)+'/release',{},auth=True)
        call('/api/staff/logout',{},auth=True)
    except Exception: pass
    for ref in refs:
        try: call('/api/bookings/cancel',{'reference':ref})
        except Exception: pass
